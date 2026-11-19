// T-0067: no Ensure/startup calls; one read-only SQLite snapshot, immutable commit witnesses.
using System.Globalization;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

public sealed partial class SqliteInventoryStore : IInventoryGenerationComparer
{
    private static void RetainGenerationCommit(SqliteConnection connection, SqliteTransaction transaction,
        string root, string generation, ReconciliationPublication publication)
    {
        using var command = InventorySchema.Command(connection, transaction, """
            INSERT INTO InventoryGenerationCommit VALUES($operation,$root,$generation,$fingerprint,$request,$journal,
                $next,$sequence,$issues,$coverage,$identity,5,1);
            """);
        command.Parameters.AddWithValue("$operation", publication.Plan.OperationId);
        command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$fingerprint", publication.Fingerprint);
        command.Parameters.AddWithValue("$request", publication.RequestHash);
        command.Parameters.AddWithValue("$journal", publication.Plan.JournalIdentity);
        command.Parameters.AddWithValue("$next", publication.Plan.NextUsn);
        command.Parameters.AddWithValue("$sequence", publication.Plan.Before.LastNoticeSequence);
        command.Parameters.AddWithValue("$issues", (int)publication.Plan.Before.Issues);
        command.Parameters.AddWithValue("$coverage", (int)publication.Plan.Coverage);
        command.Parameters.AddWithValue("$identity", (int)publication.Plan.IdentityEvidence);
        command.ExecuteNonQuery();
    }

    private sealed record ComparisonGeneration(string Journal, bool Certain, List<ComparisonEntry> Entries);
    private sealed record ComparisonEntry(IndexedInventoryMetadata Metadata, string? Identity, long Attributes)
    {
        internal string Path => Metadata.Path;
        internal bool Usable => Metadata.IsAvailable && !string.IsNullOrWhiteSpace(Identity);
    }

    public Task<InventoryComparisonResult> CompareAsync(InventoryComparisonRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        PreparedInventorySnapshot.ValidateCaptureIdentity(request.RootIdentity);
        foreach (var generation in new[] { request.BeforeGeneration, request.AfterGeneration })
            if (!Guid.TryParseExact(generation, "N", out var id) || id.ToString("N") != generation)
                throw new InventoryIndexException("inventory.comparison_invalid_request");
        if (!Enum.IsDefined(request.BeforeCoverage) || !Enum.IsDefined(request.AfterCoverage))
            throw new InventoryIndexException("inventory.comparison_invalid_request");
        return Task.Run(() => Compare(request, cancellationToken), cancellationToken);
    }

    private InventoryComparisonResult Compare(InventoryComparisonRequest request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!File.Exists(paths.DatabasePath)) throw new InventoryIndexException("inventory.comparison_uncommitted");
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5 }.ToString());
        connection.Open();
        InventorySchema.Execute(connection, null, "PRAGMA query_only=ON;");
        using var transaction = connection.BeginTransaction(deferred: true);
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version is < 5 or > 6) throw new InventoryIndexException("inventory.comparison_schema_unsupported");
        SqliteApplicationStore.ValidateMigration(connection, transaction, version);
        var before = ReadComparisonGeneration(connection, transaction, request.RootIdentity, request.BeforeGeneration, token);
        var after = ReadComparisonGeneration(connection, transaction, request.RootIdentity, request.AfterGeneration, token);
        if (before.Journal != after.Journal) throw new InventoryIndexException("inventory.comparison_incompatible");
        // Validate both inputs before self-comparison, including their committed provenance.
        if (request.BeforeGeneration == request.AfterGeneration)
            return new(request.RootIdentity, before.Journal, request.BeforeGeneration, request.AfterGeneration, []);
        var certain = before.Certain && after.Certain && request.BeforeCoverage == SuppliedInventoryCoverage.Complete &&
            request.AfterCoverage == SuppliedInventoryCoverage.Complete;
        var changes = Classify(before.Entries, after.Entries, certain, token);
        return new(request.RootIdentity, before.Journal, request.BeforeGeneration, request.AfterGeneration, changes.AsReadOnly());
    }

    private static ComparisonGeneration ReadComparisonGeneration(SqliteConnection connection, SqliteTransaction transaction,
        string root, string generation, CancellationToken token)
    {
        string fingerprint; long total;
        using (var command = InventorySchema.Command(connection, transaction, """
            SELECT s.RootIdentity,s.Status,s.CommittedEntries,s.TotalEntries,coalesce(c.CanonicalFingerprint,s.Fingerprint)
            FROM InventoryScan s LEFT JOIN InventoryCapture c ON c.GenerationId=s.GenerationId WHERE s.GenerationId=$generation;
            """))
        {
            command.Parameters.AddWithValue("$generation", generation);
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InventoryIndexException("inventory.comparison_uncommitted");
            if (reader.GetString(0) != root) throw new InventoryIndexException("inventory.comparison_incompatible");
            if (reader.GetString(1) != nameof(InventoryRunStatus.Completed) || reader.GetInt64(2) != reader.GetInt64(3))
                throw new InventoryIndexException("inventory.comparison_incomplete");
            total = reader.GetInt64(3); fingerprint = reader.GetString(4);
            if (total is < 0 or > 1_100_000 || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
                throw new InventoryIndexException("inventory.comparison_invalid_state");
        }
        string? journal = null; var certain = true; var witnesses = 0;
        using (var command = InventorySchema.Command(connection, transaction, """
            SELECT Fingerprint,JournalIdentity,Coverage,IdentityEvidence,SourceSchema,ComparisonFormat,
                OperationId,RequestHash,NextUsn,NoticeSequence,Issues
            FROM InventoryGenerationCommit WHERE RootIdentity=$root AND GenerationId=$generation;
            """))
        {
            command.Parameters.AddWithValue("$root", root); command.Parameters.AddWithValue("$generation", generation);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (++witnesses > 1_100_000) throw new InventoryIndexException("inventory.comparison_invalid_state");
                var thisJournal = reader.GetString(1); PreparedInventorySnapshot.ValidateCaptureIdentity(thisJournal);
                if (reader.GetString(0) != fingerprint || (journal is not null && journal != thisJournal))
                    throw new InventoryIndexException("inventory.comparison_incompatible");
                if (reader.GetInt32(4) is not (4 or 5) || reader.GetInt32(5) != 1)
                    throw new InventoryIndexException("inventory.comparison_schema_unsupported");
                if (!Guid.TryParseExact(reader.GetString(6), "N", out _) || reader.GetString(7).Length != 64 ||
                    !reader.GetString(7).All(Uri.IsHexDigit) || reader.GetInt64(8) < 0 || reader.GetInt64(9) < 0 ||
                    reader.GetInt32(10) is < 0 or > 31 || reader.GetInt32(2) is < 0 or > 2 || reader.GetInt32(3) is < 0 or > 1)
                    throw new InventoryIndexException("inventory.comparison_invalid_state");
                journal = thisJournal;
                certain &= reader.GetInt32(2) == (int)SuppliedInventoryCoverage.Complete &&
                    reader.GetInt32(3) == (int)InventoryIdentityEvidence.SuppliedStableWithinJournal;
            }
        }
        if (journal is null) throw new InventoryIndexException("inventory.comparison_uncommitted");
        var entries = new List<ComparisonEntry>();
        using (var command = InventorySchema.Command(connection, transaction, """
            SELECT RelativePath,Kind,Length,ModifiedUtc,ErrorCode,ObjectIdentity,Attributes
            FROM InventoryDirectoryEntry WHERE GenerationId=$generation ORDER BY RelativePath COLLATE BINARY;
            """))
        {
            command.Parameters.AddWithValue("$generation", generation); using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                token.ThrowIfCancellationRequested();
                if (entries.Count >= total) throw new InventoryIndexException("inventory.comparison_incomplete");
                var metadata = IndexedInventoryMetadata.Read(reader.GetValue(0), reader.GetValue(1), reader.GetValue(2),
                    reader.GetValue(3), reader.GetValue(4));
                var rawIdentity = reader.GetValue(5); var rawAttributes = reader.GetValue(6);
                if ((rawIdentity is not DBNull && rawIdentity is not string) || rawAttributes is not long attributes ||
                    attributes is < 0 or > uint.MaxValue)
                    throw new InventoryIndexException("inventory.comparison_invalid_state");
                var identity = rawIdentity as string;
                if (identity is not null && (identity.Length > 3200 || identity.Any(char.IsControl)))
                    throw new InventoryIndexException("inventory.comparison_invalid_state");
                entries.Add(new(metadata, identity, attributes));
            }
        }
        if (entries.Count != total) throw new InventoryIndexException("inventory.comparison_incomplete");
        return new(journal, certain, entries);
    }

    private static List<InventoryDifference> Classify(List<ComparisonEntry> before, List<ComparisonEntry> after,
        bool certain, CancellationToken token)
    {
        var oldByPath = before.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var newByPath = after.ToDictionary(e => e.Path, StringComparer.Ordinal);
        var oldIds = before.Where(e => e.Usable).GroupBy(e => e.Identity!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var newIds = after.Where(e => e.Usable).GroupBy(e => e.Identity!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        var result = new List<InventoryDifference>();
        bool Unique(ComparisonEntry entry) => entry.Usable &&
            (!oldIds.TryGetValue(entry.Identity!, out var a) || a.Length == 1) &&
            (!newIds.TryGetValue(entry.Identity!, out var b) || b.Length == 1) &&
            (!oldIds.TryGetValue(entry.Identity!, out var x) || !newIds.TryGetValue(entry.Identity!, out var y) ||
                x[0].Metadata.Kind == y[0].Metadata.Kind);
        static bool SameMetadata(ComparisonEntry a, ComparisonEntry b) => a.Metadata.Kind == b.Metadata.Kind &&
            a.Metadata.Length == b.Metadata.Length && a.Metadata.ModifiedUtc == b.Metadata.ModifiedUtc && a.Attributes == b.Attributes;
        if (certain)
        {
            foreach (var old in before)
            {
                token.ThrowIfCancellationRequested();
                if (!Unique(old) || !newIds.TryGetValue(old.Identity!, out var matches) || matches.Length != 1) continue;
                var next = matches[0];
                if (!Unique(next) || old.Metadata.Kind != next.Metadata.Kind) continue;
                oldByPath.Remove(old.Path); newByPath.Remove(next.Path);
                var changed = !SameMetadata(old, next);
                if (old.Path != next.Path) result.Add(new(InventoryDifferenceKind.Renamed, old.Path, next.Path, changed, "stable_identity"));
                else if (changed) result.Add(new(InventoryDifferenceKind.Changed, old.Path, next.Path, true, "metadata_changed"));
            }
        }
        foreach (var path in oldByPath.Keys.Union(newByPath.Keys, StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            oldByPath.TryGetValue(path, out var old); newByPath.TryGetValue(path, out var next);
            var proved = certain && (old is null || Unique(old)) && (next is null || Unique(next));
            if (old is not null && next is not null && old.Identity == next.Identity && old.Metadata.Kind != next.Metadata.Kind) proved = false;
            var kind = !proved ? InventoryDifferenceKind.Unknown : old is null ? InventoryDifferenceKind.Added :
                next is null ? InventoryDifferenceKind.Removed : InventoryDifferenceKind.Changed;
            result.Add(new(kind, old?.Path, next?.Path, old is not null && next is not null && !SameMetadata(old, next),
                proved ? "stable_identity" : !certain ? "unverified_coverage_or_identity" : "unproved_entry_identity"));
        }
        return result.OrderBy(d => d.BeforePath ?? d.AfterPath, StringComparer.Ordinal)
            .ThenBy(d => d.AfterPath, StringComparer.Ordinal).ThenBy(d => d.Kind).ToList();
    }
}
