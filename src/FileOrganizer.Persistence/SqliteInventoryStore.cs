using System.Globalization;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

/// <summary>Stages only bounded supplied observations. Caller holds the existing exclusive profile lease.</summary>
public sealed partial class SqliteInventoryStore(AppDataPaths paths) : IInventoryStore, IInventoryReconciliationStore
{
    private readonly SemaphoreSlim writer = new(1, 1);

    public Task<InventoryIndexResult> IndexAsync(InventorySnapshot snapshot, int batchSize, CancellationToken cancellationToken,
        IProgress<InventoryIndexResult>? progress = null,
        Func<CancellationToken, Task<InventorySnapshot>>? recapture = null)
    {
        var prepared = PreparedInventorySnapshot.Create(snapshot);
        if (batchSize is < 1 or > 64) throw new InventoryIndexException("inventory.invalid_batch_size");
        return Task.Run(() => WithLock(() => Index(prepared, batchSize, cancellationToken, progress, recapture), cancellationToken), cancellationToken);
    }

    public Task<InventoryIndexState> ReadStateAsync(CancellationToken cancellationToken)
        => Task.Run(() => WithLock(() => ReadState(cancellationToken), cancellationToken), cancellationToken);

    private T WithLock<T>(Func<T> action, CancellationToken token)
    {
        writer.Wait(token);
        try { return action(); }
        catch (SqliteException exception)
        {
            throw new InventoryIndexException(exception.SqliteErrorCode is 11 or 26 ? "inventory.corrupt" : "inventory.unavailable");
        }
        finally { writer.Release(); }
    }

    private InventoryIndexResult Index(PreparedInventorySnapshot snapshot, int batchSize, CancellationToken token,
        IProgress<InventoryIndexResult>? progress, Func<CancellationToken, Task<InventorySnapshot>>? recapture,
        ReconciliationPublication? reconciliation = null)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(paths.DatabasePath))
            new SqliteApplicationStore(paths).InitializeAsync(token).GetAwaiter().GetResult();
        using var connection = Open();
        InventorySchema.Ensure(connection, paths.DataDirectory, token);
        string generation;
        int cursor;
        var reuseCompleted = false;
        using (var transaction = connection.BeginTransaction())
        {
            InventorySchema.Execute(connection, transaction, "UPDATE InventoryScan SET Status='Interrupted' WHERE Status='Running';");
            using var find = InventorySchema.Command(connection, transaction,
                "SELECT GenerationId,Status,CommittedEntries,TotalEntries FROM InventoryScan WHERE RootIdentity=$root AND Fingerprint=$fingerprint;");
            find.Parameters.AddWithValue("$root", snapshot.RootIdentity);
            find.Parameters.AddWithValue("$fingerprint", snapshot.Fingerprint);
            using var reader = find.ExecuteReader();
            if (reader.Read())
            {
                generation = reader.GetString(0);
                var status = Enum.Parse<InventoryRunStatus>(reader.GetString(1));
                cursor = reader.GetInt32(2);
                if (reader.GetInt32(3) != snapshot.Entries.Count || cursor < 0 || cursor > snapshot.Entries.Count)
                    throw new InventoryIndexException("inventory.invalid_checkpoint");
                reader.Close();
                ValidatePrefix(connection, transaction, generation, snapshot, cursor);
                if (status is InventoryRunStatus.Completed or InventoryRunStatus.Incomplete)
                {
                    if (cursor != snapshot.Entries.Count)
                        throw new InventoryIndexException("inventory.invalid_checkpoint");
                    if (status == InventoryRunStatus.Completed)
                    {
                        if (snapshot.HasErrors)
                            throw new InventoryIndexException("inventory.invalid_checkpoint");
                        reuseCompleted = true;
                    }
                }
                if (!reuseCompleted) SetStatus(connection, transaction, generation, InventoryRunStatus.Running);
            }
            else
            {
                reader.Close();
                generation = Guid.NewGuid().ToString("N");
                cursor = 0;
                using var insert = InventorySchema.Command(connection, transaction,
                    "INSERT INTO InventoryScan VALUES($generation,$root,$fingerprint,'Running',0,$total);");
                insert.Parameters.AddWithValue("$generation", generation);
                insert.Parameters.AddWithValue("$root", snapshot.RootIdentity);
                insert.Parameters.AddWithValue("$fingerprint", snapshot.Fingerprint);
                insert.Parameters.AddWithValue("$total", snapshot.Entries.Count);
                insert.ExecuteNonQuery();
            }
            token.ThrowIfCancellationRequested();
            transaction.Commit();
        }

        // Completed generations are immutable history. Failed recapture cannot demote them or
        // repoint an active root; they pass the same fresh-source barrier before reuse.
        if (reuseCompleted)
        {
            token.ThrowIfCancellationRequested();
            var validationError = ValidateRecapture(snapshot, recapture, token);
            if (validationError is not null) throw new InventoryIndexException(validationError);
            using var publish = connection.BeginTransaction();
            ValidatePrefix(connection, publish, generation, snapshot, cursor);
            token.ThrowIfCancellationRequested();
            Publish(connection, publish, snapshot.RootIdentity, generation, reconciliation);
            token.ThrowIfCancellationRequested();
            publish.Commit();
            return Result(InventoryRunStatus.Completed, true, cursor);
        }

        try
        {
            while (cursor < snapshot.Entries.Count)
            {
                token.ThrowIfCancellationRequested();
                var end = Math.Min(snapshot.Entries.Count, cursor + batchSize);
                using (var batch = connection.BeginTransaction())
                {
                    for (var ordinal = cursor; ordinal < end; ordinal++)
                    {
                        token.ThrowIfCancellationRequested();
                        InsertEntry(connection, batch, generation, ordinal, snapshot.Entries[ordinal]);
                    }
                    using var checkpoint = InventorySchema.Command(connection, batch,
                        "UPDATE InventoryScan SET CommittedEntries=$cursor WHERE GenerationId=$generation AND Status='Running';");
                    checkpoint.Parameters.AddWithValue("$cursor", end);
                    checkpoint.Parameters.AddWithValue("$generation", generation);
                    if (checkpoint.ExecuteNonQuery() != 1) throw new InventoryIndexException("inventory.invalid_checkpoint");
                    token.ThrowIfCancellationRequested();
                    batch.Commit();
                }
                cursor = end;
                progress?.Report(Result(InventoryRunStatus.Running, false, cursor));
            }
            token.ThrowIfCancellationRequested();
            var validationError = snapshot.HasErrors ? "inventory.source_incomplete" : ValidateRecapture(snapshot, recapture, token);
            var canPublish = validationError is null;
            var finalStatus = canPublish ? InventoryRunStatus.Completed : InventoryRunStatus.Incomplete;
            using (var finish = connection.BeginTransaction())
            {
                ValidatePrefix(connection, finish, generation, snapshot, cursor);
                SetStatus(connection, finish, generation, finalStatus);
                if (canPublish)
                {
                    Publish(connection, finish, snapshot.RootIdentity, generation, reconciliation);
                }
                token.ThrowIfCancellationRequested();
                finish.Commit();
            }
            return Result(finalStatus, canPublish, cursor);
        }
        catch (Exception original)
        {
            try
            {
                SetStatus(connection, null, generation, original is OperationCanceledException
                    ? InventoryRunStatus.Cancelled : InventoryRunStatus.Interrupted);
            }
            catch (Exception statusFailure)
            {
                throw new AggregateException("inventory.failure_state_unavailable", original, statusFailure);
            }
            throw;
        }

        InventoryIndexResult Result(InventoryRunStatus status, bool published, int committed)
            => new(generation, snapshot.Fingerprint, status, committed, snapshot.Entries.Count, published);
    }

    private InventoryIndexState ReadState(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(paths.DatabasePath)) return new InventoryIndexState([], []);
        using var connection = Open();
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > 5) throw new StoreInitializationException("store.newer_schema");
        if (version < 1) throw new StoreInitializationException("store.invalid_schema");
        SqliteApplicationStore.ValidateMigration(connection, null, version);
        if (version == 1) return new InventoryIndexState([], []);
        using var transaction = connection.BeginTransaction();
        var roots = new List<InventoryRootState>();
        using (var command = InventorySchema.Command(connection, transaction, """
            SELECT r.RootIdentity,r.GenerationId,
                (SELECT count(*) FROM InventoryFileObject o WHERE o.GenerationId=r.GenerationId),
                (SELECT count(*) FROM InventoryDirectoryEntry e WHERE e.GenerationId=r.GenerationId)
            FROM InventoryPublishedRoot r ORDER BY r.RootIdentity LIMIT 257;
            """))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (roots.Count == 256) throw new InventoryIndexException("inventory.query_limit");
                roots.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3)));
            }
        var runs = new List<InventoryScanState>();
        using (var command = InventorySchema.Command(connection, transaction, version >= 3
            ? "SELECT s.GenerationId,s.RootIdentity,coalesce(c.CanonicalFingerprint,s.Fingerprint),s.Status,s.CommittedEntries,s.TotalEntries FROM InventoryScan s LEFT JOIN InventoryCapture c ON c.GenerationId=s.GenerationId ORDER BY s.RootIdentity,s.Fingerprint LIMIT 257;"
            : "SELECT GenerationId,RootIdentity,Fingerprint,Status,CommittedEntries,TotalEntries FROM InventoryScan ORDER BY RootIdentity,Fingerprint LIMIT 257;"))
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (runs.Count == 256) throw new InventoryIndexException("inventory.query_limit");
                runs.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    Enum.Parse<InventoryRunStatus>(reader.GetString(3)), reader.GetInt32(4), reader.GetInt32(5)));
            }
        return new InventoryIndexState(roots.AsReadOnly(), runs.AsReadOnly());
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            InventorySchema.Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000; PRAGMA synchronous=FULL;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static void SetStatus(SqliteConnection connection, SqliteTransaction? transaction, string generation, InventoryRunStatus status)
    {
        using var command = InventorySchema.Command(connection, transaction,
            "UPDATE InventoryScan SET Status=$status WHERE GenerationId=$generation AND Status<>'Completed';");
        command.Parameters.AddWithValue("$status", status.ToString());
        command.Parameters.AddWithValue("$generation", generation);
        if (command.ExecuteNonQuery() != 1) throw new InventoryIndexException("inventory.invalid_checkpoint");
    }

    private static string? ValidateRecapture(PreparedInventorySnapshot expected,
        Func<CancellationToken, Task<InventorySnapshot>>? recapture, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        // A direct caller supplies immutable observations only; no live-source consistency claim
        // is made. InventoryIndexer always supplies recapture for a real source.
        if (recapture is null) return null;
        try
        {
            var actual = PreparedInventorySnapshot.Create(recapture(token).GetAwaiter().GetResult());
            token.ThrowIfCancellationRequested();
            return actual.HasErrors || actual.RootIdentity != expected.RootIdentity || actual.Fingerprint != expected.Fingerprint
                ? "inventory.source_changed" : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            token.ThrowIfCancellationRequested();
            return "inventory.source_validation_failed";
        }
    }

    private static void Publish(SqliteConnection connection, SqliteTransaction transaction, string rootIdentity, string generation,
        ReconciliationPublication? reconciliation = null)
    {
        GuardReconciliationPublication(connection, transaction, rootIdentity, generation, reconciliation);
        using var publish = InventorySchema.Command(connection, transaction,
            "INSERT INTO InventoryPublishedRoot(RootIdentity,GenerationId) VALUES($root,$generation) ON CONFLICT(RootIdentity) DO UPDATE SET GenerationId=excluded.GenerationId;");
        publish.Parameters.AddWithValue("$root", rootIdentity);
        publish.Parameters.AddWithValue("$generation", generation);
        publish.ExecuteNonQuery();
        CompleteReconciliationPublication(connection, transaction, rootIdentity, generation, reconciliation);
    }

    private static void ValidatePrefix(SqliteConnection connection, SqliteTransaction transaction, string generation,
        PreparedInventorySnapshot snapshot, int cursor)
    {
        using var command = InventorySchema.Command(connection, transaction, """
            SELECT e.Ordinal,e.RelativePath,e.Kind,e.ObjectIdentity,e.Length,e.ModifiedUtc,e.Attributes,e.ErrorCode,
                   o.Kind,o.Length,o.ModifiedUtc,o.Attributes,e.ParentPath,e.Name
            FROM InventoryDirectoryEntry e LEFT JOIN InventoryFileObject o
                ON o.GenerationId=e.GenerationId AND o.ObjectIdentity=e.ObjectIdentity
            WHERE e.GenerationId=$generation ORDER BY e.Ordinal;
            """);
        command.Parameters.AddWithValue("$generation", generation);
        using var reader = command.ExecuteReader();
        var ordinal = 0;
        while (reader.Read())
        {
            if (ordinal >= cursor || reader.GetInt32(0) != ordinal)
                throw new InventoryIndexException("inventory.invalid_checkpoint");
            var entry = snapshot.Entries[ordinal];
            var identity = entry.Kind != InventoryEntryKind.ExcludedReparse && entry.ErrorCode is null ? entry.ObjectIdentity : null;
            var mtime = entry.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture);
            var slash = entry.RelativePath.LastIndexOf('/');
            if (reader.GetString(1) != entry.RelativePath || reader.GetInt32(2) != (int)entry.Kind ||
                (reader.IsDBNull(3) ? null : reader.GetString(3)) != identity || reader.GetInt64(4) != entry.Length ||
                reader.GetString(5) != mtime || reader.GetInt64(6) != entry.Attributes ||
                (reader.IsDBNull(7) ? null : reader.GetString(7)) != entry.ErrorCode ||
                reader.GetString(12) != (slash < 0 ? string.Empty : entry.RelativePath[..slash]) ||
                reader.GetString(13) != entry.RelativePath[(slash + 1)..] ||
                (identity is not null && (reader.IsDBNull(8) || reader.GetInt32(8) != (int)entry.Kind ||
                    reader.GetInt64(9) != entry.Length || reader.GetString(10) != mtime || reader.GetInt64(11) != entry.Attributes)))
                throw new InventoryIndexException("inventory.invalid_checkpoint");
            ordinal++;
        }
        if (ordinal != cursor) throw new InventoryIndexException("inventory.invalid_checkpoint");
        reader.Close();
        using var objects = InventorySchema.Command(connection, transaction,
            "SELECT count(*) FROM InventoryFileObject WHERE GenerationId=$generation;");
        objects.Parameters.AddWithValue("$generation", generation);
        var expectedObjects = snapshot.Entries.Take(cursor).Where(entry => entry.Kind != InventoryEntryKind.ExcludedReparse && entry.ErrorCode is null)
            .Select(entry => entry.ObjectIdentity).Distinct(StringComparer.Ordinal).Count();
        if (Convert.ToInt32(objects.ExecuteScalar(), CultureInfo.InvariantCulture) != expectedObjects)
            throw new InventoryIndexException("inventory.invalid_checkpoint");
    }

    private static void InsertEntry(SqliteConnection connection, SqliteTransaction transaction, string generation, int ordinal, InventoryEntry entry)
    {
        var objectIdentity = entry.Kind != InventoryEntryKind.ExcludedReparse && entry.ErrorCode is null ? entry.ObjectIdentity : null;
        if (objectIdentity is not null)
        {
            using var file = InventorySchema.Command(connection, transaction,
                "INSERT INTO InventoryFileObject VALUES($generation,$object,$kind,$length,$mtime,$attrs) ON CONFLICT(GenerationId,ObjectIdentity) DO NOTHING;");
            AddMetadata(file);
            file.Parameters.AddWithValue("$object", objectIdentity);
            file.ExecuteNonQuery();
        }
        var slash = entry.RelativePath.LastIndexOf('/');
        using var command = InventorySchema.Command(connection, transaction,
            "INSERT INTO InventoryDirectoryEntry VALUES($generation,$ordinal,$path,$parent,$name,$kind,$object,$length,$mtime,$attrs,$error);");
        AddMetadata(command);
        command.Parameters.AddWithValue("$ordinal", ordinal);
        command.Parameters.AddWithValue("$path", entry.RelativePath);
        command.Parameters.AddWithValue("$parent", slash < 0 ? string.Empty : entry.RelativePath[..slash]);
        command.Parameters.AddWithValue("$name", entry.RelativePath[(slash + 1)..]);
        command.Parameters.AddWithValue("$object", (object?)objectIdentity ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)entry.ErrorCode ?? DBNull.Value);
        command.ExecuteNonQuery();

        void AddMetadata(SqliteCommand statement)
        {
            statement.Parameters.AddWithValue("$generation", generation);
            statement.Parameters.AddWithValue("$kind", (int)entry.Kind);
            statement.Parameters.AddWithValue("$length", entry.Length);
            statement.Parameters.AddWithValue("$mtime", entry.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture));
            statement.Parameters.AddWithValue("$attrs", entry.Attributes);
        }
    }
}
