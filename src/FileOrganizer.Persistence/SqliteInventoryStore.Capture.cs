using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

public sealed partial class SqliteInventoryStore
{
    public Task<InventoryIndexResult> CaptureAsync(IInventoryCaptureSource source, InventoryCaptureOptions options,
        string? resumeGenerationId, CancellationToken cancellationToken, IProgress<InventoryCaptureProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (resumeGenerationId is not null && !Guid.TryParseExact(resumeGenerationId, "N", out _))
            throw new InventoryIndexException("inventory.capture_invalid_resume");
        return Task.Run(() => WithLock(() => Capture(source, options, resumeGenerationId, cancellationToken, progress), cancellationToken), cancellationToken);
    }

    private InventoryIndexResult Capture(IInventoryCaptureSource source, InventoryCaptureOptions options, string? resume,
        CancellationToken token, IProgress<InventoryCaptureProgress>? progress)
    {
        token.ThrowIfCancellationRequested();
        var budget = new CaptureBudget(paths, options);
        budget.Check(token);
        IInventoryCaptureLease lease;
        try
        {
            lease = source.OpenReadLeaseAsync(token).GetAwaiter().GetResult()
                ?? throw new InventoryIndexException("inventory.capture_unsupported");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (InventoryIndexException) { throw; }
        catch (Exception) { throw new InventoryIndexException("inventory.capture_source_failed"); }
        try
        {
            PreparedInventorySnapshot.ValidateCaptureIdentity(lease.RootIdentity);
            budget.Check(token);
            if (!File.Exists(paths.DatabasePath))
                new SqliteApplicationStore(paths).InitializeAsync(token).GetAwaiter().GetResult();
            using var connection = Open();
            InventorySchema.Execute(connection, null, "PRAGMA cache_size=-32768; PRAGMA temp_store=MEMORY;");
            InventoryCaptureSchema.Ensure(connection, paths.DataDirectory, token);
            budget.Check(token);
            var generation = BeginCapture(connection, lease.RootIdentity, options, resume, budget, token);
            try
            {
                var phase = ReadCaptureText(connection, generation, "Phase");
                if (phase == "Capturing") CaptureDirectories(connection, lease, generation, options, budget, token, progress);
                budget.Check(token);
                if (CaptureNumber(connection, generation, "CoverageErrors") > 0)
                    return FinishCapture(connection, generation, lease.RootIdentity, false, budget, options, token);
                VerifyDirectories(connection, lease, generation, options, budget, token, progress);
                budget.Check(token);
                var complete = CaptureNumber(connection, generation, "CoverageErrors") == 0;
                return FinishCapture(connection, generation, lease.RootIdentity, complete, budget, options, token);
            }
            catch (Exception original)
            {
                try
                {
                    using var transaction = connection.BeginTransaction();
                    var incomplete = original is InventoryIndexException safety && safety.Code.StartsWith("inventory.capture_", StringComparison.Ordinal);
                    SetStatus(connection, transaction, generation, original is OperationCanceledException
                        ? InventoryRunStatus.Cancelled : incomplete ? InventoryRunStatus.Incomplete : InventoryRunStatus.Interrupted);
                    if (incomplete)
                    {
                        using var mark = InventorySchema.Command(connection, transaction,
                            "UPDATE InventoryCapture SET Phase='Incomplete' WHERE GenerationId=$generation;");
                        mark.Parameters.AddWithValue("$generation", generation);
                        mark.ExecuteNonQuery();
                    }
                    transaction.Commit();
                }
                catch (Exception failure) { throw new AggregateException("inventory.capture_failure_state_unavailable", original, failure); }
                throw;
            }
        }
        finally { lease.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static string BeginCapture(SqliteConnection connection, string root, InventoryCaptureOptions options,
        string? resume, CaptureBudget budget, CancellationToken token)
    {
        var optionsHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(options)));
        using var transaction = connection.BeginTransaction();
        var generation = resume ?? Guid.NewGuid().ToString("N");
        if (resume is null)
        {
            using var scan = InventorySchema.Command(connection, transaction,
                "INSERT INTO InventoryScan VALUES($generation,$root,$fingerprint,'Running',0,0);");
            scan.Parameters.AddWithValue("$generation", generation);
            scan.Parameters.AddWithValue("$root", root);
            scan.Parameters.AddWithValue("$fingerprint", "capture:" + generation);
            scan.ExecuteNonQuery();
            using var capture = InventorySchema.Command(connection, transaction,
                "INSERT INTO InventoryCapture(GenerationId,Phase,OptionsHash,DeadlineUtc) VALUES($generation,'Capturing',$options,$deadline);");
            capture.Parameters.AddWithValue("$generation", generation);
            capture.Parameters.AddWithValue("$options", optionsHash);
            capture.Parameters.AddWithValue("$deadline", budget.Deadline.ToString("O", CultureInfo.InvariantCulture));
            capture.ExecuteNonQuery();
            using var directory = InventorySchema.Command(connection, transaction,
                "INSERT INTO InventoryCaptureDirectory(GenerationId,RelativePath,ExpectedIdentity,State) VALUES($generation,'',$root,'Pending');");
            directory.Parameters.AddWithValue("$generation", generation);
            directory.Parameters.AddWithValue("$root", root);
            directory.ExecuteNonQuery();
        }
        else
        {
            using var command = InventorySchema.Command(connection, transaction, """
                SELECT s.RootIdentity,s.Status,c.OptionsHash,c.DeadlineUtc,c.Phase
                FROM InventoryScan s JOIN InventoryCapture c ON c.GenerationId=s.GenerationId WHERE s.GenerationId=$generation;
                """);
            command.Parameters.AddWithValue("$generation", generation);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InventoryIndexException("inventory.capture_invalid_resume");
            if (reader.GetString(0) != root) throw new InventoryIndexException("inventory.source_changed");
            if (reader.GetString(2) != optionsHash) throw new InventoryIndexException("inventory.capture_options_changed");
            if (reader.GetString(1) == "Completed") throw new InventoryIndexException("inventory.capture_already_completed");
            if (reader.GetString(4) == "Incomplete") throw new InventoryIndexException("inventory.capture_incomplete_requires_new_run");
            budget.Deadline = DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture);
            reader.Close();
            budget.Check(token);
            using (var counts = InventorySchema.Command(connection, transaction, """
                SELECT c.CapturedDirectories,s.CommittedEntries,
                    (SELECT count(*) FROM InventoryDirectoryEntry e WHERE e.GenerationId=s.GenerationId AND e.Kind=1),
                    (SELECT count(*) FROM InventoryDirectoryEntry e WHERE e.GenerationId=s.GenerationId),
                    (SELECT count(*) FROM InventoryCaptureDirectory d WHERE d.GenerationId=s.GenerationId)
                FROM InventoryScan s JOIN InventoryCapture c ON c.GenerationId=s.GenerationId WHERE s.GenerationId=$generation;
                """))
            {
                counts.Parameters.AddWithValue("$generation", generation);
                using var state = counts.ExecuteReader();
                if (!state.Read() || state.GetInt32(0) != state.GetInt32(2) || state.GetInt32(0) > options.MaxDirectories ||
                    state.GetInt32(1) != state.GetInt32(3) || state.GetInt32(1) > options.MaxEntries || state.GetInt32(4) > state.GetInt32(0) + 1)
                    throw new InventoryIndexException("inventory.capture_invalid_checkpoint");
            }
            SetStatus(connection, transaction, generation, InventoryRunStatus.Running);
            using var reset = InventorySchema.Command(connection, transaction,
                "UPDATE InventoryCaptureDirectory SET State='Pending' WHERE GenerationId=$generation AND State='Enumerating';");
            reset.Parameters.AddWithValue("$generation", generation);
            reset.ExecuteNonQuery();
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return generation;
    }

    private static void CaptureDirectories(SqliteConnection connection, IInventoryCaptureLease lease, string generation,
        InventoryCaptureOptions options, CaptureBudget budget, CancellationToken token, IProgress<InventoryCaptureProgress>? progress)
    {
        while (NextDirectory(connection, generation, false) is { } directory)
        {
            budget.Check(token);
            var epoch = Guid.NewGuid().ToString("N");
            using (var begin = InventorySchema.Command(connection, null,
                "UPDATE InventoryCaptureDirectory SET State='Enumerating',CaptureEpoch=$epoch WHERE GenerationId=$generation AND RelativePath=$path;"))
            {
                begin.Parameters.AddWithValue("$epoch", epoch);
                begin.Parameters.AddWithValue("$generation", generation);
                begin.Parameters.AddWithValue("$path", directory.Path);
                begin.ExecuteNonQuery();
            }
            IInventoryDirectoryReader? reader = null;
            try
            {
                reader = lease.OpenDirectoryAsync(directory.Path, directory.Identity, token).GetAwaiter().GetResult();
                if (reader.DirectoryIdentity != directory.Identity) throw new InventoryIndexException("inventory.source_changed");
                while (true)
                {
                    budget.Check(token);
                    InventoryCapturePage page;
                    try { page = reader.ReadPageAsync(options.PageSize, token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception) { MarkDirectoryError(connection, generation, directory.Path, false); break; }
                    ValidatePage(page, directory.Path, options);
                    CommitCapturePage(connection, generation, directory.Path, epoch, page, options, token);
                    budget.Check(token);
                    ReportCapture(connection, generation, InventoryCapturePhase.Capturing, progress);
                    if (page.EndOfDirectory) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (InventoryIndexException exception) when (exception.Code == "inventory.source_changed")
            { MarkDirectoryError(connection, generation, directory.Path, false); }
            catch (Exception) when (reader is null)
            { MarkDirectoryError(connection, generation, directory.Path, false); }
            finally { reader?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }

    private static void ValidatePage(InventoryCapturePage page, string parent, InventoryCaptureOptions options)
    {
        if (page is null || page.Entries is null || page.Entries.Count > options.PageSize ||
            (page.Entries.Count == 0 && !page.EndOfDirectory))
            throw new InventoryIndexException("inventory.capture_invalid_page");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in page.Entries)
        {
            PreparedInventorySnapshot.ValidateCaptureEntry(entry, options);
            var slash = entry.RelativePath.LastIndexOf('/');
            if ((slash < 0 ? string.Empty : entry.RelativePath[..slash]) != parent || !names.Add(entry.RelativePath))
                throw new InventoryIndexException("inventory.capture_invalid_page");
        }
    }

    private static (string Path, string Identity)? NextDirectory(SqliteConnection connection, string generation, bool verify)
    {
        using var command = InventorySchema.Command(connection, null, verify
            ? "SELECT RelativePath,ExpectedIdentity FROM InventoryCaptureDirectory WHERE GenerationId=$generation AND VerificationState='Pending' ORDER BY RelativePath LIMIT 1;"
            : "SELECT RelativePath,ExpectedIdentity FROM InventoryCaptureDirectory WHERE GenerationId=$generation AND State='Pending' ORDER BY RelativePath LIMIT 1;");
        command.Parameters.AddWithValue("$generation", generation);
        using var reader = command.ExecuteReader();
        return reader.Read() ? (reader.GetString(0), reader.GetString(1)) : null;
    }

    private static string ReadCaptureText(SqliteConnection connection, string generation, string column)
    {
        if (column is not ("Phase" or "CanonicalFingerprint")) throw new ArgumentException("Unsupported capture field.", nameof(column));
        using var command = InventorySchema.Command(connection, null, $"SELECT {column} FROM InventoryCapture WHERE GenerationId=$generation;");
        command.Parameters.AddWithValue("$generation", generation);
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static int CaptureNumber(SqliteConnection connection, string generation, string column)
    {
        if (column is not ("CoverageErrors" or "VerifiedEntries")) throw new ArgumentException("Unsupported capture field.", nameof(column));
        using var command = InventorySchema.Command(connection, null, $"SELECT {column} FROM InventoryCapture WHERE GenerationId=$generation;");
        command.Parameters.AddWithValue("$generation", generation);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed class CaptureBudget(AppDataPaths profilePaths, InventoryCaptureOptions limits)
    {
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        public DateTimeOffset Deadline { get; set; } = DateTimeOffset.UtcNow + limits.MaxDuration;

        public void Check(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow > Deadline || elapsed.Elapsed > limits.MaxDuration)
                throw new InventoryIndexException("inventory.capture_time_limit");
            long total = 0;
            foreach (var (path, ceiling) in new[]
            {
                (profilePaths.DatabasePath, limits.MaxDatabaseBytes),
                (profilePaths.DatabasePath + "-wal", limits.MaxWalBytes),
                (profilePaths.DatabasePath + "-shm", 16L * 1024 * 1024),
            })
            {
                var length = File.Exists(path) ? new FileInfo(path).Length : 0;
                if (length > ceiling) throw new InventoryIndexException("inventory.capture_storage_limit");
                total = checked(total + length);
            }
            var backupDirectory = Path.Combine(profilePaths.DataDirectory, "backups");
            if (Directory.Exists(backupDirectory))
            {
                var count = 0;
                foreach (var path in Directory.EnumerateFiles(backupDirectory, "before-inventory-v*.db"))
                {
                    if (++count > 16) throw new InventoryIndexException("inventory.capture_backup_limit");
                    total = checked(total + new FileInfo(path).Length);
                }
            }
            if (total > limits.MaxRunBytes) throw new InventoryIndexException("inventory.capture_storage_limit");
            if (new DriveInfo(Path.GetPathRoot(profilePaths.RootDirectory)!).AvailableFreeSpace < limits.MinimumFreeBytes)
                throw new InventoryIndexException("inventory.capture_free_space");
            using var process = Process.GetCurrentProcess();
            if (process.WorkingSet64 > limits.MaxResidentBytes) throw new InventoryIndexException("inventory.capture_memory_limit");
        }
    }
}
