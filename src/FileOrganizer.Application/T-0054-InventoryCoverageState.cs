// T-0054 / FO-006 item 1. Supplied metadata only: no source access or reconciliation writes.
namespace FileOrganizer.Application;

public enum InventoryCoverage { Unknown, Stale, Offline }
public enum InventoryNoticeKind { Changed, WatcherOverflow, JournalDiscontinuity, SourceUnavailable }
[Flags]
public enum InventoryCoverageIssues
{
    None = 0, ChangeNotice = 1, WatcherOverflow = 2, JournalGap = 4, SourceUnavailable = 8, UnboundCheckpoint = 16,
}

/// <summary>Supplied association to already committed observations; never inferred from an entry count.</summary>
public sealed record InventoryCommittedCheckpoint(string RootIdentity, string GenerationId,
    string ObservationFingerprint, string JournalIdentity, long NextUsn);
public sealed record InventoryJournalRange(string JournalIdentity, long FirstUsn, long LowestValidUsn, long NextUsn);
public sealed record InventoryChangeNotice(string RootIdentity, long Sequence, InventoryNoticeKind Kind,
    InventoryJournalRange? Journal = null);
public sealed record InventoryCoverageReceipt(string RootIdentity, string GenerationId, string ObservationFingerprint,
    InventoryCommittedCheckpoint? Checkpoint, long LastNoticeSequence, InventoryCoverageIssues Issues);
public sealed record InventoryCoverageFloor(string RootIdentity, string GenerationId, string ObservationFingerprint,
    long MinimumNoticeSequence, InventoryCoverageIssues RequiredIssues);

/// <summary>
/// Immutable, conservative hint state over one accepted published generation. Hints cannot change
/// observations, advance a committed checkpoint, establish freshness, or authorize deletion.
/// The caller supplies trusted committed-reference/floor metadata. This model cannot prove that
/// metadata was durably committed or detect a coherent rollback of both receipt and trusted anchor.
/// Durable checkpoint/publication composition belongs to FO-006 item 2, not this type.
/// </summary>
public sealed class InventoryCoverageState
{
    private const InventoryCoverageIssues KnownIssues = InventoryCoverageIssues.ChangeNotice |
        InventoryCoverageIssues.WatcherOverflow | InventoryCoverageIssues.JournalGap |
        InventoryCoverageIssues.SourceUnavailable | InventoryCoverageIssues.UnboundCheckpoint;

    private InventoryCoverageState(InventoryRootState root, InventoryScanState scan,
        InventoryCommittedCheckpoint? checkpoint, long sequence, InventoryCoverageIssues issues)
    {
        PublishedRoot = root;
        PublishedScan = scan;
        Checkpoint = checkpoint;
        LastNoticeSequence = sequence;
        Issues = issues;
    }

    public InventoryRootState PublishedRoot { get; }
    public InventoryScanState PublishedScan { get; }
    public InventoryCommittedCheckpoint? Checkpoint { get; }
    public long LastNoticeSequence { get; }
    public InventoryCoverageIssues Issues { get; }
    public InventoryCoverage Coverage => (Issues & InventoryCoverageIssues.SourceUnavailable) != 0
        ? InventoryCoverage.Offline : Issues != InventoryCoverageIssues.None ? InventoryCoverage.Stale : InventoryCoverage.Unknown;
    // Initial-scan completion is not proof of current live coverage. Item 1 never marks it fresh.
    public bool RequiresReconciliation => true;
    public InventoryCoverageFloor Floor => new(PublishedRoot.RootIdentity, PublishedRoot.GenerationId,
        PublishedScan.Fingerprint, LastNoticeSequence, Issues);

    public static InventoryCoverageState Create(InventoryRootState root, InventoryScanState scan,
        InventoryCommittedCheckpoint? checkpoint = null)
    {
        if (root is null || scan is null || !ValidGeneration(root.GenerationId) ||
            root.RootIdentity != scan.RootIdentity || root.GenerationId != scan.GenerationId ||
            scan.Status != InventoryRunStatus.Completed || scan.CommittedEntries != scan.TotalEntries ||
            root.EntryCount != scan.TotalEntries || root.EntryCount is < 0 or > 1_100_000 ||
            root.ObjectCount < 0 || root.ObjectCount > root.EntryCount || !ValidFingerprint(scan.Fingerprint))
            throw new InventoryIndexException("inventory.coverage_invalid_publication");
        PreparedInventorySnapshot.ValidateCaptureIdentity(root.RootIdentity);
        if (checkpoint is not null)
        {
            if (checkpoint.RootIdentity != root.RootIdentity || checkpoint.GenerationId != root.GenerationId ||
                checkpoint.ObservationFingerprint != scan.Fingerprint || checkpoint.NextUsn < 0)
                throw new InventoryIndexException("inventory.coverage_invalid_checkpoint");
            PreparedInventorySnapshot.ValidateCaptureIdentity(checkpoint.JournalIdentity);
        }
        return new(root, scan, checkpoint, 0, InventoryCoverageIssues.None);
    }

    public InventoryCoverageState Apply(InventoryChangeNotice notice, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(notice);
        if (notice.RootIdentity != PublishedRoot.RootIdentity)
            throw new InventoryIndexException("inventory.coverage_wrong_root");
        if (notice.Sequence <= 0 || !Enum.IsDefined(notice.Kind))
            throw new InventoryIndexException("inventory.coverage_invalid_notice");
        var added = notice.Kind switch
        {
            InventoryNoticeKind.Changed => InventoryCoverageIssues.ChangeNotice,
            InventoryNoticeKind.WatcherOverflow => InventoryCoverageIssues.WatcherOverflow,
            InventoryNoticeKind.JournalDiscontinuity => InventoryCoverageIssues.JournalGap,
            InventoryNoticeKind.SourceUnavailable => InventoryCoverageIssues.SourceUnavailable,
            _ => throw new InventoryIndexException("inventory.coverage_invalid_notice"),
        };
        if (notice.Journal is { } range)
        {
            PreparedInventorySnapshot.ValidateCaptureIdentity(range.JournalIdentity);
            // FirstUsn and LowestValidUsn are independent lower bounds; do not invent an ordering.
            if (range.FirstUsn < 0 || range.LowestValidUsn < 0 || range.NextUsn < 0 ||
                range.FirstUsn > range.NextUsn || range.LowestValidUsn > range.NextUsn)
                throw new InventoryIndexException("inventory.coverage_invalid_journal_range");
            if (Checkpoint is null) added |= InventoryCoverageIssues.UnboundCheckpoint;
            else if (range.JournalIdentity != Checkpoint.JournalIdentity ||
                Checkpoint.NextUsn < Math.Max(range.FirstUsn, range.LowestValidUsn) || Checkpoint.NextUsn > range.NextUsn)
                added |= InventoryCoverageIssues.JournalGap;
        }
        // Union is bounded and order-independent. Even late loss/offline notices remain conservative;
        // a duplicate or newer ordinary hint cannot clear uncertainty or make an offline source online.
        var sequence = Math.Max(LastNoticeSequence, notice.Sequence);
        var issues = Issues | added;
        cancellationToken.ThrowIfCancellationRequested();
        if (sequence == LastNoticeSequence && issues == Issues) return this;
        return new(PublishedRoot, PublishedScan, Checkpoint, sequence, issues);
    }

    public InventoryCoverageReceipt Export() => new(PublishedRoot.RootIdentity, PublishedRoot.GenerationId,
        PublishedScan.Fingerprint, Checkpoint, LastNoticeSequence, Issues);

    /// <summary>
    /// Validates a serialized receipt against separately supplied current publication/checkpoint and
    /// a trusted monotonic floor. The floor must not be derived from the receipt being checked.
    /// This is a host restart-validation seam, not durable reconciliation or authenticated storage.
    /// </summary>
    public static InventoryCoverageState Restore(InventoryRootState root, InventoryScanState scan,
        InventoryCommittedCheckpoint? checkpoint, InventoryCoverageReceipt receipt, InventoryCoverageFloor floor)
    {
        var baseline = Create(root, scan, checkpoint);
        if (receipt is null || floor is null || floor.RootIdentity != root.RootIdentity ||
            floor.GenerationId != root.GenerationId || floor.ObservationFingerprint != scan.Fingerprint ||
            receipt.RootIdentity != root.RootIdentity ||
            receipt.GenerationId != root.GenerationId || receipt.ObservationFingerprint != scan.Fingerprint ||
            receipt.Checkpoint != checkpoint || receipt.LastNoticeSequence < 0 ||
            !ValidIssues(receipt.Issues) || !ValidIssues(floor.RequiredIssues) || floor.MinimumNoticeSequence < 0 ||
            (receipt.LastNoticeSequence == 0 && receipt.Issues != InventoryCoverageIssues.None) ||
            (receipt.LastNoticeSequence > 0 && receipt.Issues == InventoryCoverageIssues.None))
            throw new InventoryIndexException("inventory.coverage_invalid_receipt");
        if (receipt.LastNoticeSequence < floor.MinimumNoticeSequence ||
            (receipt.Issues & floor.RequiredIssues) != floor.RequiredIssues)
            throw new InventoryIndexException("inventory.coverage_rollback_refused");
        return new(baseline.PublishedRoot, baseline.PublishedScan, baseline.Checkpoint,
            receipt.LastNoticeSequence, receipt.Issues);
    }

    private static bool ValidGeneration(string? value) => value is not null &&
        Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static bool ValidFingerprint(string? value) => value is { Length: 64 } && value.All(Uri.IsHexDigit);
    private static bool ValidIssues(InventoryCoverageIssues value) => (value & ~KnownIssues) == 0;
}
