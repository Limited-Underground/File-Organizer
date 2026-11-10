// T-0069 / FO-012: immutable host query views over completed-generation comparison.
using System.Security.Cryptography;
using System.Text.Json;

namespace FileOrganizer.Application;

public sealed record SnapshotComparisonSelection(InventoryComparisonRequest Snapshots,
    InventoryDifferenceKind? Classification = null, IndexedInventoryFilter? Filter = null);
public sealed record SnapshotComparisonCounts(int Added, int Removed, int Renamed, int Changed, int Unknown)
{
    public int Total => checked(Added + Removed + Renamed + Changed + Unknown);
    internal static SnapshotComparisonCounts From(IEnumerable<InventoryDifference> rows)
    {
        var counts = new int[5];
        foreach (var row in rows)
        {
            if (!Enum.IsDefined(row.Kind)) throw new InventoryIndexException("inventory.snapshot_invalid_result");
            counts[(int)row.Kind]++;
        }
        return new(counts[0], counts[1], counts[2], counts[3], counts[4]);
    }
}
public sealed record SnapshotComparisonCursor(string ViewIdentity, int PageSize, int NextIndex);
public sealed record SnapshotComparisonPage(IReadOnlyList<InventoryDifference> Rows,
    SnapshotComparisonCursor? NextCursor, SnapshotComparisonCounts TotalCounts, SnapshotComparisonCounts FilteredCounts);

/// <summary>
/// A fully evaluated, immutable selected-generation view. Counts distinguish the complete comparison
/// from its filtered differences; Unknown is always its own class. No unchanged count is inferred.
/// </summary>
public sealed class SnapshotComparisonView
{
    private readonly InventoryDifference[] rows;
    internal SnapshotComparisonView(SnapshotComparisonSelection selection, string journal,
        InventoryDifference[] all, InventoryDifference[] filtered)
    {
        Selection = selection; JournalIdentity = journal; rows = filtered;
        TotalCounts = SnapshotComparisonCounts.From(all); FilteredCounts = SnapshotComparisonCounts.From(filtered);
        ViewIdentity = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { selection, journal, all, filtered })));
    }
    public SnapshotComparisonSelection Selection { get; }
    public string JournalIdentity { get; }
    public string ViewIdentity { get; }
    public SnapshotComparisonCounts TotalCounts { get; }
    public SnapshotComparisonCounts FilteredCounts { get; }
    public bool IsAtomicSnapshot => false;
    public bool LiveAvailabilityKnown => false;

    public SnapshotComparisonPage ReadPage(int pageSize = 50, SnapshotComparisonCursor? after = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (pageSize is < 1 or > 200) throw new InventoryIndexException("inventory.snapshot_invalid_page");
        var offset = 0;
        if (after is not null)
        {
            if (after.ViewIdentity != ViewIdentity || after.PageSize != pageSize || after.NextIndex <= 0 ||
                after.NextIndex >= rows.Length || after.NextIndex % pageSize != 0)
                throw new InventoryIndexException("inventory.snapshot_cursor_mismatch");
            offset = after.NextIndex;
        }
        var page = rows.Skip(offset).Take(pageSize).ToArray();
        var end = offset + page.Length;
        var next = end < rows.Length ? new SnapshotComparisonCursor(ViewIdentity, pageSize, end) : null;
        cancellationToken.ThrowIfCancellationRequested();
        return new(Array.AsReadOnly(page), next, TotalCounts, FilteredCounts);
    }
}

/// <summary>
/// No SQL, schema changes or source access. The comparer owns admission and classification; the
/// existing reader owns saved-query semantics and its typed evaluation limits. A saved filter matches
/// a difference when ALL its predicates match either complete endpoint (old OR new). Classification
/// is ANDed afterwards. Renames can thus match their former or new name without mixing endpoint fields.
/// </summary>
public sealed class InventorySnapshotComparisons(IInventoryGenerationComparer comparer, IIndexedInventoryReader reader)
{
    public async Task<SnapshotComparisonView> OpenAsync(SnapshotComparisonSelection selection,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selection); ArgumentNullException.ThrowIfNull(selection.Snapshots);
        cancellationToken.ThrowIfCancellationRequested();
        if (selection.Classification is { } kind && !Enum.IsDefined(kind))
            throw new InventoryIndexException("inventory.snapshot_invalid_filter");
        var filter = IndexedInventoryFilter.Normalize(selection.Filter);
        selection = selection with { Filter = filter };
        // Refusal/cancellation propagates; none is translated into an empty successful view.
        var result = await comparer.CompareAsync(selection.Snapshots, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (result.RootIdentity != selection.Snapshots.RootIdentity ||
            result.BeforeGeneration != selection.Snapshots.BeforeGeneration || result.AfterGeneration != selection.Snapshots.AfterGeneration)
            throw new InventoryIndexException("inventory.snapshot_selection_mismatch");
        var all = result.Differences.OrderBy(d => d.BeforePath ?? d.AfterPath, StringComparer.Ordinal)
            .ThenBy(d => d.AfterPath, StringComparer.Ordinal).ThenBy(d => d.Kind).ToArray();
        _ = SnapshotComparisonCounts.From(all);
        HashSet<string>? before = null, after = null;
        if (filter.IsActive && all.Length != 0)
        {
            before = await ReadMatches(selection.Snapshots.RootIdentity, selection.Snapshots.BeforeGeneration, filter, cancellationToken).ConfigureAwait(false);
            after = await ReadMatches(selection.Snapshots.RootIdentity, selection.Snapshots.AfterGeneration, filter, cancellationToken).ConfigureAwait(false);
        }
        var filtered = new List<InventoryDifference>();
        foreach (var row in all)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((selection.Classification is null || row.Kind == selection.Classification) &&
                (!filter.IsActive || (row.BeforePath is not null && before!.Contains(row.BeforePath)) ||
                    (row.AfterPath is not null && after!.Contains(row.AfterPath))))
                filtered.Add(row);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new(selection, result.JournalIdentity, all, filtered.ToArray());
    }

    private async Task<HashSet<string>> ReadMatches(string root, string generation, IndexedInventoryFilter filter, CancellationToken token)
    {
        var matched = new HashSet<string>(StringComparer.Ordinal);
        IndexedInventoryCursor? cursor = null;
        int? entryCount = null;
        string? priorPath = null;
        do
        {
            token.ThrowIfCancellationRequested();
            var page = await reader.ReadPageAsync(new(root, generation, 200, cursor, filter), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (page.Catalog.RootIdentity != root || page.Catalog.GenerationId != generation || page.Rows.Count > 200 ||
                page.Catalog.EntryCount is < 0 or > 1_100_000 || (entryCount is not null && entryCount != page.Catalog.EntryCount))
                throw new InventoryIndexException("inventory.snapshot_selection_mismatch");
            entryCount = page.Catalog.EntryCount;
            foreach (var row in page.Rows)
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrEmpty(row.RelativePath) ||
                    (priorPath is not null && StringComparer.Ordinal.Compare(row.RelativePath, priorPath) <= 0) ||
                    !matched.Add(row.RelativePath) || matched.Count > entryCount)
                    throw new InventoryIndexException("inventory.snapshot_invalid_page");
                priorPath = row.RelativePath;
            }
            cursor = page.NextCursor;
            if (cursor is not null && (page.Rows.Count == 0 || cursor.RootIdentity != root || cursor.GenerationId != generation ||
                cursor.PageSize != 200 || cursor.AfterRelativePath != priorPath || IndexedInventoryFilter.Normalize(cursor.Filter) != filter))
                throw new InventoryIndexException("inventory.snapshot_cursor_mismatch");
        } while (cursor is not null);
        return matched;
    }
}
