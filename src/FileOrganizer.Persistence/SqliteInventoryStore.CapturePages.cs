using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

public sealed partial class SqliteInventoryStore
{
    private static void CommitCapturePage(SqliteConnection connection, string generation, string parent, string epoch,
        InventoryCapturePage page, InventoryCaptureOptions options, CancellationToken token)
    {
        using var transaction = connection.BeginTransaction();
        var cursor = ScanCount(connection, transaction, generation);
        int directoryCount;
        using (var directories = InventorySchema.Command(connection, transaction,
            "SELECT CapturedDirectories FROM InventoryCapture WHERE GenerationId=$generation;"))
        {
            directories.Parameters.AddWithValue("$generation", generation);
            directoryCount = Convert.ToInt32(directories.ExecuteScalar(), CultureInfo.InvariantCulture);
        }
        foreach (var entry in page.Entries)
        {
            token.ThrowIfCancellationRequested();
            var existing = FindCapturedEntry(connection, transaction, generation, entry.RelativePath);
            if (existing is not null)
            {
                if (existing != entry) throw new InventoryIndexException("inventory.source_changed");
                using var prior = InventorySchema.Command(connection, transaction,
                    "SELECT CaptureEpoch FROM InventoryCaptureObserved WHERE GenerationId=$generation AND RelativePath=$path;");
                prior.Parameters.AddWithValue("$generation", generation);
                prior.Parameters.AddWithValue("$path", entry.RelativePath);
                if (Convert.ToString(prior.ExecuteScalar(), CultureInfo.InvariantCulture) == epoch)
                    throw new InventoryIndexException("inventory.capture_duplicate_entry");
            }
            else
            {
                if (cursor >= options.MaxEntries) throw new InventoryIndexException("inventory.capture_entry_limit");
                using (var collision = InventorySchema.Command(connection, transaction,
                    "SELECT count(*) FROM InventoryCaptureObserved WHERE GenerationId=$generation AND PathKey=$key;"))
                {
                    collision.Parameters.AddWithValue("$generation", generation);
                    collision.Parameters.AddWithValue("$key", entry.RelativePath.ToUpperInvariant());
                    if (Convert.ToInt32(collision.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                        throw new InventoryIndexException("inventory.capture_path_collision");
                }
                ValidateCapturedObject(connection, transaction, generation, entry);
                InsertEntry(connection, transaction, generation, cursor, entry);
                cursor++;
                if (entry.ErrorCode is not null) IncrementCoverageError(connection, transaction, generation);
                if (entry.Kind == InventoryEntryKind.Directory)
                {
                    if (++directoryCount > options.MaxDirectories)
                        throw new InventoryIndexException("inventory.capture_directory_limit");
                }
                if (entry.Kind == InventoryEntryKind.Directory && entry.ErrorCode is null)
                {
                    using (var cycle = InventorySchema.Command(connection, transaction,
                        "SELECT EXISTS(SELECT 1 FROM InventoryCaptureDirectory WHERE GenerationId=$generation AND ExpectedIdentity=$identity);"))
                    {
                        cycle.Parameters.AddWithValue("$generation", generation);
                        cycle.Parameters.AddWithValue("$identity", entry.ObjectIdentity);
                        if (Convert.ToInt32(cycle.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                            throw new InventoryIndexException("inventory.source_changed");
                    }
                    using var directory = InventorySchema.Command(connection, transaction,
                        "INSERT INTO InventoryCaptureDirectory(GenerationId,RelativePath,ExpectedIdentity,State) VALUES($generation,$path,$identity,'Pending');");
                    directory.Parameters.AddWithValue("$generation", generation);
                    directory.Parameters.AddWithValue("$path", entry.RelativePath);
                    directory.Parameters.AddWithValue("$identity", entry.ObjectIdentity);
                    directory.ExecuteNonQuery();
                }
            }
            using var observed = InventorySchema.Command(connection, transaction, """
                INSERT INTO InventoryCaptureObserved(GenerationId,RelativePath,PathKey,CaptureEpoch) VALUES($generation,$path,$key,$epoch)
                ON CONFLICT(GenerationId,RelativePath) DO UPDATE SET CaptureEpoch=excluded.CaptureEpoch;
                """);
            observed.Parameters.AddWithValue("$generation", generation);
            observed.Parameters.AddWithValue("$path", entry.RelativePath);
            observed.Parameters.AddWithValue("$key", entry.RelativePath.ToUpperInvariant());
            observed.Parameters.AddWithValue("$epoch", epoch);
            observed.ExecuteNonQuery();
        }
        using (var directories = InventorySchema.Command(connection, transaction,
            "UPDATE InventoryCapture SET CapturedDirectories=$count WHERE GenerationId=$generation;"))
        {
            directories.Parameters.AddWithValue("$count", directoryCount);
            directories.Parameters.AddWithValue("$generation", generation);
            directories.ExecuteNonQuery();
        }
        using (var checkpoint = InventorySchema.Command(connection, transaction,
            "UPDATE InventoryScan SET CommittedEntries=$cursor,TotalEntries=$cursor WHERE GenerationId=$generation;"))
        {
            checkpoint.Parameters.AddWithValue("$cursor", cursor);
            checkpoint.Parameters.AddWithValue("$generation", generation);
            checkpoint.ExecuteNonQuery();
        }
        if (page.EndOfDirectory)
        {
            if (MissingSeen(connection, transaction, generation, parent, epoch, false))
                MarkDirectoryError(connection, transaction, generation, parent, false);
            else SetDirectoryComplete(connection, transaction, generation, parent, false);
        }
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static void ValidateCapturedObject(SqliteConnection connection, SqliteTransaction transaction, string generation, InventoryEntry entry)
    {
        if (entry.ErrorCode is not null || entry.Kind == InventoryEntryKind.ExcludedReparse) return;
        using var command = InventorySchema.Command(connection, transaction,
            "SELECT Kind,Length,ModifiedUtc,Attributes FROM InventoryFileObject WHERE GenerationId=$generation AND ObjectIdentity=$identity;");
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$identity", entry.ObjectIdentity);
        using var reader = command.ExecuteReader();
        if (reader.Read() && (reader.GetInt32(0) != (int)entry.Kind || reader.GetInt64(1) != entry.Length ||
            reader.GetString(2) != entry.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture) || reader.GetInt64(3) != entry.Attributes))
            throw new InventoryIndexException("inventory.source_changed");
    }

    private static InventoryEntry? FindCapturedEntry(SqliteConnection connection, SqliteTransaction transaction, string generation, string path)
    {
        using var command = InventorySchema.Command(connection, transaction,
            "SELECT RelativePath,Kind,ObjectIdentity,Length,ModifiedUtc,Attributes,ErrorCode,ParentPath,Name FROM InventoryDirectoryEntry WHERE GenerationId=$generation AND RelativePath=$path;");
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$path", path);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var entry = ReadCapturedEntry(reader);
        var slash = entry.RelativePath.LastIndexOf('/');
        if (reader.GetString(7) != (slash < 0 ? string.Empty : entry.RelativePath[..slash]) ||
            reader.GetString(8) != entry.RelativePath[(slash + 1)..])
            throw new InventoryIndexException("inventory.source_changed");
        return entry;
    }

    private static InventoryEntry ReadCapturedEntry(SqliteDataReader reader) => new(reader.GetString(0),
        (InventoryEntryKind)reader.GetInt32(1), reader.IsDBNull(2) ? string.Empty : reader.GetString(2), reader.GetInt64(3),
        DateTime.Parse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind), reader.GetInt64(5),
        reader.IsDBNull(6) ? null : reader.GetString(6));

    private static void VerifyDirectories(SqliteConnection connection, IInventoryCaptureLease lease, string generation,
        InventoryCaptureOptions options, CaptureBudget budget, CancellationToken token, IProgress<InventoryCaptureProgress>? progress)
    {
        var epoch = Guid.NewGuid().ToString("N");
        using (var transaction = connection.BeginTransaction())
        {
            using var reset = InventorySchema.Command(connection, transaction,
                "UPDATE InventoryCapture SET Phase='Verifying',VerificationEpoch=$epoch,VerifiedEntries=0 WHERE GenerationId=$generation;");
            reset.Parameters.AddWithValue("$epoch", epoch);
            reset.Parameters.AddWithValue("$generation", generation);
            reset.ExecuteNonQuery();
            using var directories = InventorySchema.Command(connection, transaction,
                "UPDATE InventoryCaptureDirectory SET VerificationState='Pending' WHERE GenerationId=$generation;");
            directories.Parameters.AddWithValue("$generation", generation);
            directories.ExecuteNonQuery();
            token.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        while (NextDirectory(connection, generation, true) is { } directory)
        {
            budget.Check(token);
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
                    catch (Exception) { MarkDirectoryError(connection, generation, directory.Path, true); break; }
                    ValidatePage(page, directory.Path, options);
                    using (var transaction = connection.BeginTransaction())
                    {
                        foreach (var entry in page.Entries)
                        {
                            token.ThrowIfCancellationRequested();
                            if (entry.ErrorCode is not null || FindCapturedEntry(connection, transaction, generation, entry.RelativePath) != entry)
                                throw new InventoryIndexException("inventory.source_changed");
                            ValidateCapturedObject(connection, transaction, generation, entry);
                            using var seen = InventorySchema.Command(connection, transaction, """
                                UPDATE InventoryCaptureObserved SET VerificationEpoch=$epoch
                                WHERE GenerationId=$generation AND RelativePath=$path AND (VerificationEpoch IS NULL OR VerificationEpoch<>$epoch);
                                """);
                            seen.Parameters.AddWithValue("$epoch", epoch);
                            seen.Parameters.AddWithValue("$generation", generation);
                            seen.Parameters.AddWithValue("$path", entry.RelativePath);
                            if (seen.ExecuteNonQuery() != 1) throw new InventoryIndexException("inventory.source_changed");
                        }
                        using var count = InventorySchema.Command(connection, transaction,
                            "UPDATE InventoryCapture SET VerifiedEntries=VerifiedEntries+$count WHERE GenerationId=$generation;");
                        count.Parameters.AddWithValue("$count", page.Entries.Count);
                        count.Parameters.AddWithValue("$generation", generation);
                        count.ExecuteNonQuery();
                        if (page.EndOfDirectory)
                        {
                            if (MissingSeen(connection, transaction, generation, directory.Path, epoch, true))
                                MarkDirectoryError(connection, transaction, generation, directory.Path, true);
                            else SetDirectoryComplete(connection, transaction, generation, directory.Path, true);
                        }
                        token.ThrowIfCancellationRequested();
                        transaction.Commit();
                    }
                    budget.Check(token);
                    ReportCapture(connection, generation, InventoryCapturePhase.Verifying, progress);
                    if (page.EndOfDirectory) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (InventoryIndexException exception) when (exception.Code == "inventory.source_changed")
            { MarkDirectoryError(connection, generation, directory.Path, true); }
            catch (Exception) when (reader is null)
            { MarkDirectoryError(connection, generation, directory.Path, true); }
            finally { reader?.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
    }

    private static bool MissingSeen(SqliteConnection connection, SqliteTransaction transaction, string generation,
        string parent, string epoch, bool verify)
    {
        using var command = InventorySchema.Command(connection, transaction, verify ? """
            SELECT EXISTS(SELECT 1 FROM InventoryDirectoryEntry e LEFT JOIN InventoryCaptureObserved o
            ON o.GenerationId=e.GenerationId AND o.RelativePath=e.RelativePath
            WHERE e.GenerationId=$generation AND e.ParentPath=$parent AND (o.VerificationEpoch IS NULL OR o.VerificationEpoch<>$epoch));
            """ : """
            SELECT EXISTS(SELECT 1 FROM InventoryDirectoryEntry e LEFT JOIN InventoryCaptureObserved o
            ON o.GenerationId=e.GenerationId AND o.RelativePath=e.RelativePath
            WHERE e.GenerationId=$generation AND e.ParentPath=$parent AND (o.CaptureEpoch IS NULL OR o.CaptureEpoch<>$epoch));
            """);
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$parent", parent);
        command.Parameters.AddWithValue("$epoch", epoch);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0;
    }

    private static void SetDirectoryComplete(SqliteConnection connection, SqliteTransaction transaction, string generation, string path, bool verify)
    {
        using var command = InventorySchema.Command(connection, transaction, verify
            ? "UPDATE InventoryCaptureDirectory SET VerificationState='Complete' WHERE GenerationId=$generation AND RelativePath=$path;"
            : "UPDATE InventoryCaptureDirectory SET State='Complete' WHERE GenerationId=$generation AND RelativePath=$path;");
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    private static void MarkDirectoryError(SqliteConnection connection, string generation, string path, bool verify)
    {
        using var transaction = connection.BeginTransaction();
        MarkDirectoryError(connection, transaction, generation, path, verify);
        transaction.Commit();
    }

    private static void MarkDirectoryError(SqliteConnection connection, SqliteTransaction transaction, string generation, string path, bool verify)
    {
        using var command = InventorySchema.Command(connection, transaction, verify
            ? "UPDATE InventoryCaptureDirectory SET VerificationState='Error',ErrorCode='inventory.capture_source_error' WHERE GenerationId=$generation AND RelativePath=$path AND VerificationState<>'Error';"
            : "UPDATE InventoryCaptureDirectory SET State='Error',ErrorCode='inventory.capture_source_error' WHERE GenerationId=$generation AND RelativePath=$path AND State<>'Error';");
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$path", path);
        if (command.ExecuteNonQuery() != 0) IncrementCoverageError(connection, transaction, generation);
    }

    private static void IncrementCoverageError(SqliteConnection connection, SqliteTransaction transaction, string generation)
    {
        using var command = InventorySchema.Command(connection, transaction,
            "UPDATE InventoryCapture SET CoverageErrors=CoverageErrors+1 WHERE GenerationId=$generation;");
        command.Parameters.AddWithValue("$generation", generation);
        command.ExecuteNonQuery();
    }

    private static int ScanCount(SqliteConnection connection, SqliteTransaction? transaction, string generation)
    {
        using var command = InventorySchema.Command(connection, transaction,
            "SELECT CommittedEntries FROM InventoryScan WHERE GenerationId=$generation;");
        command.Parameters.AddWithValue("$generation", generation);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static void ReportCapture(SqliteConnection connection, string generation, InventoryCapturePhase phase,
        IProgress<InventoryCaptureProgress>? progress)
    {
        if (progress is null) return;
        using var command = InventorySchema.Command(connection, null, """
            SELECT sum(CASE WHEN State='Complete' THEN 1 ELSE 0 END),sum(CASE WHEN State IN ('Pending','Enumerating') THEN 1 ELSE 0 END)
            FROM InventoryCaptureDirectory WHERE GenerationId=$generation;
            """);
        command.Parameters.AddWithValue("$generation", generation);
        int completed;
        int pending;
        using (var reader = command.ExecuteReader())
        {
            reader.Read();
            completed = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            pending = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
        }
        progress.Report(new(generation, phase, ScanCount(connection, null, generation), completed, pending,
            CaptureNumber(connection, generation, "VerifiedEntries")));
    }

    private static InventoryIndexResult FinishCapture(SqliteConnection connection, string generation, string root,
        bool complete, CaptureBudget budget, InventoryCaptureOptions options, CancellationToken token)
    {
        if (complete) ValidateCapturePublication(connection, generation, budget, options, token);
        var fingerprint = CaptureFingerprint(connection, generation, root, budget, options, token);
        var count = ScanCount(connection, null, generation);
        budget.Check(token);
        using var transaction = connection.BeginTransaction();
        var status = complete ? InventoryRunStatus.Completed : InventoryRunStatus.Incomplete;
        SetStatus(connection, transaction, generation, status);
        using var capture = InventorySchema.Command(connection, transaction,
            "UPDATE InventoryCapture SET Phase=$phase,CanonicalFingerprint=$fingerprint WHERE GenerationId=$generation;");
        capture.Parameters.AddWithValue("$phase", complete ? "Completed" : "Incomplete");
        capture.Parameters.AddWithValue("$fingerprint", fingerprint);
        capture.Parameters.AddWithValue("$generation", generation);
        capture.ExecuteNonQuery();
        if (complete) Publish(connection, transaction, root, generation);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
        return new(generation, fingerprint, status, count, count, complete);
    }

    private static void ValidateCapturePublication(SqliteConnection connection, string generation, CaptureBudget budget,
        InventoryCaptureOptions options, CancellationToken token)
    {
        budget.Check(token);
        using (var command = InventorySchema.Command(connection, null, """
            SELECT s.CommittedEntries,s.TotalEntries,c.VerifiedEntries,c.CoverageErrors,
                (SELECT count(*) FROM InventoryDirectoryEntry e WHERE e.GenerationId=s.GenerationId),
                (SELECT count(*) FROM InventoryCaptureObserved o WHERE o.GenerationId=s.GenerationId),
                (SELECT count(*) FROM InventoryCaptureObserved o WHERE o.GenerationId=s.GenerationId AND o.VerificationEpoch=c.VerificationEpoch),
                (SELECT count(*) FROM InventoryCaptureDirectory d WHERE d.GenerationId=s.GenerationId AND (d.State<>'Complete' OR d.VerificationState<>'Complete')),
                c.CapturedDirectories,(SELECT count(*) FROM InventoryDirectoryEntry e WHERE e.GenerationId=s.GenerationId AND e.Kind=1),s.Status,c.Phase
            FROM InventoryScan s JOIN InventoryCapture c ON c.GenerationId=s.GenerationId WHERE s.GenerationId=$generation;
            """))
        {
            command.Parameters.AddWithValue("$generation", generation);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InventoryIndexException("inventory.capture_invalid_checkpoint");
            var expected = reader.GetInt32(0);
            if (reader.GetInt32(1) != expected || reader.GetInt32(2) != expected || reader.GetInt32(3) != 0 ||
                reader.GetInt32(4) != expected || reader.GetInt32(5) != expected || reader.GetInt32(6) != expected || reader.GetInt32(7) != 0 ||
                reader.GetInt32(8) != reader.GetInt32(9) || reader.GetInt32(8) > options.MaxDirectories || expected > options.MaxEntries ||
                reader.GetString(10) != "Running" || reader.GetString(11) != "Verifying")
                throw new InventoryIndexException("inventory.capture_invalid_checkpoint");
        }
        // Each query uses generation/path/object indexes; no full-tree list or DISTINCT sort is built.
        foreach (var sql in new[]
        {
            """
            SELECT EXISTS(SELECT 1 FROM InventoryCaptureDirectory d LEFT JOIN InventoryDirectoryEntry e
                ON e.GenerationId=d.GenerationId AND e.RelativePath=d.RelativePath
            WHERE d.GenerationId=$generation AND d.RelativePath<>'' AND
                (e.RelativePath IS NULL OR e.Kind<>1 OR e.ObjectIdentity<>d.ExpectedIdentity OR e.ErrorCode IS NOT NULL));
            """,
            """
            SELECT EXISTS(SELECT 1 FROM InventoryDirectoryEntry e LEFT JOIN InventoryCaptureDirectory d
                ON d.GenerationId=e.GenerationId AND d.RelativePath=e.ParentPath
            WHERE e.GenerationId=$generation AND d.RelativePath IS NULL);
            """,
            """
            SELECT EXISTS(SELECT 1 FROM InventoryDirectoryEntry e LEFT JOIN InventoryCaptureDirectory d
                ON d.GenerationId=e.GenerationId AND d.RelativePath=e.RelativePath
            WHERE e.GenerationId=$generation AND e.Kind=1 AND e.ErrorCode IS NULL AND
                (d.RelativePath IS NULL OR d.ExpectedIdentity<>e.ObjectIdentity));
            """,
            """
            SELECT EXISTS(SELECT 1 FROM InventoryDirectoryEntry e LEFT JOIN InventoryFileObject o
                ON o.GenerationId=e.GenerationId AND o.ObjectIdentity=e.ObjectIdentity
            WHERE e.GenerationId=$generation AND e.ObjectIdentity IS NOT NULL AND
                (o.ObjectIdentity IS NULL OR o.Kind<>e.Kind OR o.Length<>e.Length OR o.ModifiedUtc<>e.ModifiedUtc OR o.Attributes<>e.Attributes));
            """,
            """
            SELECT EXISTS(SELECT 1 FROM InventoryFileObject o WHERE o.GenerationId=$generation AND NOT EXISTS
                (SELECT 1 FROM InventoryDirectoryEntry e WHERE e.GenerationId=o.GenerationId AND e.ObjectIdentity=o.ObjectIdentity));
            """,
            """
            SELECT NOT EXISTS(SELECT 1 FROM InventoryCaptureDirectory d JOIN InventoryScan s ON s.GenerationId=d.GenerationId
                WHERE d.GenerationId=$generation AND d.RelativePath='' AND d.ExpectedIdentity=s.RootIdentity);
            """,
        })
        {
            budget.Check(token);
            using var command = InventorySchema.Command(connection, null, sql);
            command.Parameters.AddWithValue("$generation", generation);
            if (Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture) != 0)
                throw new InventoryIndexException("inventory.capture_invalid_checkpoint");
        }
        budget.Check(token);
    }

    private static string CaptureFingerprint(SqliteConnection connection, string generation, string root,
        CaptureBudget budget, InventoryCaptureOptions options, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(Encoding.UTF8.GetBytes("file-organizer.capture.v1"));
        Append(Encoding.UTF8.GetBytes(root));
        using var command = InventorySchema.Command(connection, null,
            "SELECT RelativePath,Kind,ObjectIdentity,Length,ModifiedUtc,Attributes,ErrorCode FROM InventoryDirectoryEntry WHERE GenerationId=$generation ORDER BY RelativePath COLLATE BINARY;");
        command.Parameters.AddWithValue("$generation", generation);
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (count++ % options.PageSize == 0) budget.Check(token);
            Append(JsonSerializer.SerializeToUtf8Bytes(ReadCapturedEntry(reader)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());

        void Append(ReadOnlySpan<byte> value)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
            hash.AppendData(length);
            hash.AppendData(value);
        }
    }
}
