using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using FileOrganizer.Application;

namespace FileOrganizer.Persistence;

/// <summary>One active filtered SQL evaluation; no persistent state or configurable query framework.</summary>
internal sealed class IndexedFilterEvaluation
{
    private const int CandidateLimit = 1_100_000;
    private static readonly TimeSpan EvaluationLimit = TimeSpan.FromSeconds(5);
    private readonly IndexedInventoryFilter filter;
    private readonly CancellationToken cancellation;
    private readonly Action<int>? observer;
    private readonly long startedAt;
    private int candidates;

    internal IndexedFilterEvaluation(IndexedInventoryFilter filter, CancellationToken cancellation, Action<int>? observer = null)
        : this(filter, cancellation, Stopwatch.GetTimestamp(), 0, observer) { }

    // Deterministic tests seed an elapsed clock/counter without sleeping or weakening production limits.
    internal IndexedFilterEvaluation(IndexedInventoryFilter filter, CancellationToken cancellation,
        long startedAtTimestamp, int initialCandidateCount, Action<int>? observer = null)
    {
        this.filter = IndexedInventoryFilter.Normalize(filter);
        this.cancellation = cancellation;
        this.observer = observer;
        startedAt = startedAtTimestamp;
        if (initialCandidateCount < 0) throw new ArgumentOutOfRangeException(nameof(initialCandidateCount));
        candidates = initialCandidateCount;
    }

    internal string? FailureCode { get; private set; }

    internal bool Match(string field, long kind)
        => Match(field, kind, 0L, "2020-01-01T00:00:00.0000000Z", null);

    internal bool Match(object? rawPath, object? rawKind, object? rawLength, object? rawModified, object? rawError)
    {
        cancellation.ThrowIfCancellationRequested();
        if (candidates >= CandidateLimit) Fail("inventory.query_scan_limit");
        candidates++;
        observer?.Invoke(candidates);
        cancellation.ThrowIfCancellationRequested();
        if (Stopwatch.GetElapsedTime(startedAt) > EvaluationLimit) Fail("inventory.query_time_limit");
        IndexedInventoryMetadata entry;
        try { entry = IndexedInventoryMetadata.Read(rawPath, rawKind, rawLength, rawModified, rawError); }
        catch (InventoryIndexException error) { Fail(error.Code); throw; }
        var field = entry.Path;
        var target = filter.Scope == IndexedInventorySearchScope.Name
            ? field.AsSpan(field.LastIndexOf('/') + 1) : field.AsSpan();
        var day = DateOnly.FromDateTime(entry.ModifiedUtc);
        return (filter.Kind is null || entry.Kind == filter.Kind.Value) &&
            (filter.Metadata == IndexedInventoryMetadataMode.Any ||
                (filter.Metadata == IndexedInventoryMetadataMode.Available) == entry.IsAvailable) &&
            ((filter.MinBytes is null && filter.MaxBytes is null) ||
                (entry.IsAvailable && entry.Kind == InventoryEntryKind.File &&
                    (filter.MinBytes is null || entry.Length >= filter.MinBytes) && (filter.MaxBytes is null || entry.Length <= filter.MaxBytes))) &&
            ((filter.ModifiedFrom is null && filter.ModifiedThrough is null) ||
                (entry.IsAvailable && (filter.ModifiedFrom is null || day >= filter.ModifiedFrom) &&
                    (filter.ModifiedThrough is null || day <= filter.ModifiedThrough))) &&
            (filter.Text.Length == 0 || target.Contains(filter.Text.AsSpan(), filter.CaseMode == IndexedInventoryCaseMode.MatchCase
                ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase));
    }

    [DoesNotReturn]
    private void Fail(string code)
    {
        FailureCode ??= code;
        throw new InventoryIndexException(FailureCode);
    }
}
