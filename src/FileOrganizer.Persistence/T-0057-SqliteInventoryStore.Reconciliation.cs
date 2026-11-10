// T-0057: the accepted ingestion path owns staging; only its final transaction publishes this pair.
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

internal enum InventoryReconciliationStage { BeforeMigrationCommit, BeforePublish, BetweenPublishAndCheckpoint, AfterBoth }

public sealed partial class SqliteInventoryStore
{
    private readonly Action<InventoryReconciliationStage>? reconciliationObserver;
    internal SqliteInventoryStore(AppDataPaths paths, Action<InventoryReconciliationStage> observer) : this(paths)
        => reconciliationObserver = observer;

    private sealed record ReconciliationPublication(InventoryReconciliationPlan Plan, string Fingerprint,
        string RequestHash, Action<InventoryReconciliationStage>? Observe);

    public Task<InventoryReconciliationState> ReconcileAsync(InventorySnapshot snapshot, InventoryReconciliationPlan plan,
        int batchSize, CancellationToken cancellationToken, Func<CancellationToken, Task<InventorySnapshot>> recapture)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(plan.Before);
        ArgumentNullException.ThrowIfNull(recapture);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Coverage != SuppliedInventoryCoverage.Complete)
            throw new InventoryIndexException("inventory.reconciliation_unverified_coverage");
        if (!Guid.TryParseExact(plan.OperationId, "N", out var operation) || operation.ToString("N") != plan.OperationId ||
            !Enum.IsDefined(plan.IdentityEvidence) || plan.NextUsn < 0 || batchSize is < 1 or > 64)
            throw new InventoryIndexException("inventory.reconciliation_invalid_request");
        PreparedInventorySnapshot.ValidateCaptureIdentity(plan.JournalIdentity);
        var prepared = PreparedInventorySnapshot.Create(snapshot);
        if (prepared.RootIdentity != plan.Before.RootIdentity || prepared.HasErrors)
            throw new InventoryIndexException("inventory.reconciliation_incomplete");
        // Keep the default pre-v5 operation hash byte-compatible for exact retry after migration.
        var bytes = plan.IdentityEvidence == InventoryIdentityEvidence.Unverified
            ? JsonSerializer.SerializeToUtf8Bytes(new { plan = new { plan.OperationId, plan.Before,
                plan.JournalIdentity, plan.NextUsn, plan.Coverage }, prepared.Fingerprint })
            : JsonSerializer.SerializeToUtf8Bytes(new { plan, prepared.Fingerprint });
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        var publication = new ReconciliationPublication(plan, prepared.Fingerprint, hash, reconciliationObserver);
        return Task.Run(() => WithLock(() => Reconcile(prepared, publication, batchSize, cancellationToken, recapture), cancellationToken), cancellationToken);
    }

    private InventoryReconciliationState Reconcile(PreparedInventorySnapshot snapshot, ReconciliationPublication publication,
        int batchSize, CancellationToken token, Func<CancellationToken, Task<InventorySnapshot>> recapture)
    {
        if (!File.Exists(paths.DatabasePath))
            throw new InventoryIndexException("inventory.reconciliation_missing_publication");
        using var connection = Open();
        InventoryComparisonSchema.Ensure(connection, paths.DataDirectory, token,
            () => reconciliationObserver?.Invoke(InventoryReconciliationStage.BeforeMigrationCommit));
        using (var before = connection.BeginTransaction(deferred: true))
        {
            var current = ReadReconciliation(connection, before, snapshot.RootIdentity);
            if (current?.OperationId == publication.Plan.OperationId)
            {
                if (current.RequestHash != publication.RequestHash)
                    throw new InventoryIndexException("inventory.reconciliation_conflict");
                var replayError = ValidateRecapture(snapshot, recapture, token);
                if (replayError is not null) throw new InventoryIndexException(replayError);
                return current;
            }
            ValidateReconciliationBefore(connection, before, publication, current);
        }
        var result = Index(snapshot, batchSize, token, null, recapture, publication);
        if (!result.Published) throw new InventoryIndexException("inventory.reconciliation_incomplete");
        using var readback = connection.BeginTransaction(deferred: true);
        var adopted = ReadReconciliation(connection, readback, snapshot.RootIdentity);
        if (adopted is null || adopted.OperationId != publication.Plan.OperationId || adopted.RequestHash != publication.RequestHash)
            throw new InventoryIndexException("inventory.reconciliation_conflict");
        return adopted;
    }

    public Task<InventoryReconciliationState?> ReadReconciliationAsync(string rootIdentity, CancellationToken cancellationToken)
    {
        PreparedInventorySnapshot.ValidateCaptureIdentity(rootIdentity);
        return Task.Run(() => WithLock(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(paths.DatabasePath)) return null;
            using var connection = Open();
            var version = Convert.ToInt32(InventorySchema.Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > 5) throw new StoreInitializationException("store.newer_schema");
            SqliteApplicationStore.ValidateMigration(connection, null, version);
            if (version < 4) return null;
            using var transaction = connection.BeginTransaction(deferred: true);
            var result = ReadReconciliation(connection, transaction, rootIdentity);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }, cancellationToken), cancellationToken);
    }

    private static (InventoryRootState Root, InventoryScanState Scan) ReadPublished(
        SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        using var command = InventorySchema.Command(connection, transaction, """
            SELECT p.GenerationId,coalesce(c.CanonicalFingerprint,s.Fingerprint),s.Status,s.CommittedEntries,s.TotalEntries,
                (SELECT count(*) FROM InventoryFileObject o WHERE o.GenerationId=p.GenerationId),
                (SELECT count(*) FROM InventoryDirectoryEntry e WHERE e.GenerationId=p.GenerationId),s.RootIdentity
            FROM InventoryPublishedRoot p JOIN InventoryScan s ON s.GenerationId=p.GenerationId
            LEFT JOIN InventoryCapture c ON c.GenerationId=p.GenerationId WHERE p.RootIdentity=$root;
            """);
        command.Parameters.AddWithValue("$root", root);
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(7) != root)
            throw new InventoryIndexException("inventory.reconciliation_missing_publication");
        var published = new InventoryRootState(root, reader.GetString(0), reader.GetInt32(5), reader.GetInt32(6));
        var scan = new InventoryScanState(published.GenerationId, root, reader.GetString(1),
            Enum.Parse<InventoryRunStatus>(reader.GetString(2)), reader.GetInt32(3), reader.GetInt32(4));
        _ = InventoryCoverageState.Create(published, scan);
        return (published, scan);
    }

    private static InventoryReconciliationState? ReadReconciliation(SqliteConnection connection, SqliteTransaction transaction, string root)
    {
        string generation, fingerprint, operation, requestHash, journal;
        long next, sequence; InventoryCoverageIssues issues;
        using (var command = InventorySchema.Command(connection, transaction,
            "SELECT GenerationId,Fingerprint,OperationId,RequestHash,JournalIdentity,NextUsn,NoticeSequence,Issues FROM InventoryReconciliation WHERE RootIdentity=$root;"))
        {
            command.Parameters.AddWithValue("$root", root);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;
            generation = reader.GetString(0); fingerprint = reader.GetString(1); operation = reader.GetString(2);
            requestHash = reader.GetString(3); journal = reader.GetString(4); next = reader.GetInt64(5);
            sequence = reader.GetInt64(6); issues = (InventoryCoverageIssues)reader.GetInt32(7);
        }
        var (published, scan) = ReadPublished(connection, transaction, root);
        if (generation != published.GenerationId || fingerprint != scan.Fingerprint || requestHash.Length != 64 ||
            !requestHash.All(Uri.IsHexDigit) || !Guid.TryParseExact(operation, "N", out _))
            throw new InventoryIndexException("inventory.reconciliation_invalid_state");
        var checkpoint = new InventoryCommittedCheckpoint(root, generation, fingerprint, journal, next);
        var receipt = new InventoryCoverageReceipt(root, generation, fingerprint, checkpoint, sequence, issues);
        var floor = new InventoryCoverageFloor(root, generation, fingerprint, sequence, issues);
        _ = InventoryCoverageState.Restore(published, scan, checkpoint, receipt, floor);
        return new(operation, requestHash, published, scan, receipt);
    }

    private static void ValidateReconciliationBefore(SqliteConnection connection, SqliteTransaction transaction,
        ReconciliationPublication publication, InventoryReconciliationState? current)
    {
        var plan = publication.Plan;
        var (root, scan) = ReadPublished(connection, transaction, plan.Before.RootIdentity);
        var checkpoint = current?.Receipt.Checkpoint;
        var floor = current?.Floor ?? new InventoryCoverageFloor(root.RootIdentity, root.GenerationId,
            scan.Fingerprint, 0, InventoryCoverageIssues.None);
        _ = InventoryCoverageState.Restore(root, scan, checkpoint, plan.Before, floor);
        if (checkpoint is not null && ((checkpoint.JournalIdentity == plan.JournalIdentity && plan.NextUsn < checkpoint.NextUsn) ||
            (checkpoint.JournalIdentity != plan.JournalIdentity && !plan.Before.Issues.HasFlag(InventoryCoverageIssues.JournalGap))))
            throw new InventoryIndexException("inventory.reconciliation_checkpoint_refused");
    }

    private static void GuardReconciliationPublication(SqliteConnection connection, SqliteTransaction transaction,
        string root, string generation, ReconciliationPublication? publication)
    {
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version < 4) return;
        var current = ReadReconciliation(connection, transaction, root);
        if (publication is null)
        {
            if (current is not null && current.Root.GenerationId != generation)
                throw new InventoryIndexException("inventory.reconciliation_required");
            return;
        }
        if (current?.OperationId == publication.Plan.OperationId)
        {
            if (current.RequestHash != publication.RequestHash)
                throw new InventoryIndexException("inventory.reconciliation_conflict");
        }
        else ValidateReconciliationBefore(connection, transaction, publication, current);
        publication.Observe?.Invoke(InventoryReconciliationStage.BeforePublish);
    }

    private static void CompleteReconciliationPublication(SqliteConnection connection, SqliteTransaction transaction,
        string root, string generation, ReconciliationPublication? publication)
    {
        if (publication is null) return;
        publication.Observe?.Invoke(InventoryReconciliationStage.BetweenPublishAndCheckpoint);
        using var write = InventorySchema.Command(connection, transaction, """
            INSERT INTO InventoryReconciliation VALUES($root,$generation,$fingerprint,$operation,$request,$journal,$next,$sequence,$issues)
            ON CONFLICT(RootIdentity) DO UPDATE SET GenerationId=excluded.GenerationId,Fingerprint=excluded.Fingerprint,
                OperationId=excluded.OperationId,RequestHash=excluded.RequestHash,JournalIdentity=excluded.JournalIdentity,
                NextUsn=excluded.NextUsn,NoticeSequence=excluded.NoticeSequence,Issues=excluded.Issues;
            """);
        write.Parameters.AddWithValue("$root", root); write.Parameters.AddWithValue("$generation", generation);
        write.Parameters.AddWithValue("$fingerprint", publication.Fingerprint); write.Parameters.AddWithValue("$operation", publication.Plan.OperationId);
        write.Parameters.AddWithValue("$request", publication.RequestHash); write.Parameters.AddWithValue("$journal", publication.Plan.JournalIdentity);
        write.Parameters.AddWithValue("$next", publication.Plan.NextUsn); write.Parameters.AddWithValue("$sequence", publication.Plan.Before.LastNoticeSequence);
        write.Parameters.AddWithValue("$issues", (int)publication.Plan.Before.Issues);
        if (write.ExecuteNonQuery() != 1) throw new InventoryIndexException("inventory.reconciliation_invalid_state");
        RetainGenerationCommit(connection, transaction, root, generation, publication);
        publication.Observe?.Invoke(InventoryReconciliationStage.AfterBoth);
    }
}
