// T-0054: supplied-hint state only, using FO-003 ownership and FO-005 disposable ingestion.
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class T0054InventoryCoverageTests
{
    [Fact]
    public async Task DuplicateNoticesAreIdempotentAndCannotAdvanceTheCommittedCursor()
    {
        var seed = await Sample();
        var baseline = seed.Start();
        Assert.Equal(InventoryCoverage.Unknown, baseline.Coverage);
        var notice = new InventoryChangeNotice(seed.Root.RootIdentity, 5, InventoryNoticeKind.Changed);
        var changed = baseline.Apply(notice);
        Assert.Same(changed, changed.Apply(notice));
        Assert.Equal(InventoryCoverage.Stale, changed.Coverage);
        Assert.Equal(seed.Cursor, changed.Checkpoint);
        Assert.True(changed.RequiresReconciliation);
        await AssertRetained(seed);
    }

    [Fact]
    public async Task ReorderedNoticesRetainLateLossAndOfflineEvidenceWithoutGoingOnline()
    {
        var seed = await Sample();
        var notices = new[] {
            new InventoryChangeNotice(seed.Root.RootIdentity, 9, InventoryNoticeKind.Changed),
            new InventoryChangeNotice(seed.Root.RootIdentity, 3, InventoryNoticeKind.SourceUnavailable),
            new InventoryChangeNotice(seed.Root.RootIdentity, 7, InventoryNoticeKind.WatcherOverflow),
        };
        var forward = seed.Start(); var reversed = seed.Start();
        foreach (var notice in notices) forward = forward.Apply(notice);
        foreach (var notice in notices.Reverse()) reversed = reversed.Apply(notice);
        Assert.Equal(forward.Export(), reversed.Export());
        Assert.Equal(9L, forward.LastNoticeSequence);
        Assert.Equal(InventoryCoverage.Offline, forward.Coverage);
        Assert.Equal(seed.Cursor, forward.Checkpoint);
        await AssertRetained(seed);
    }

    [Fact]
    public async Task WatcherOverflowMarksUncertainCoverageWithoutDeletingPublishedObservations()
    {
        var seed = await Sample();
        var state = seed.Start().Apply(new(seed.Root.RootIdentity, 1, InventoryNoticeKind.WatcherOverflow));
        Assert.True(state.Issues.HasFlag(InventoryCoverageIssues.WatcherOverflow));
        Assert.Equal(InventoryCoverage.Stale, state.Coverage);
        Assert.Same(seed.Root, state.PublishedRoot);
        Assert.Equal(seed.Cursor, state.Checkpoint);
        await AssertRetained(seed);
    }

    [Theory]
    [InlineData("replacement-journal", 0L, 0L, 20L)]
    [InlineData("fixture-journal", 11L, 0L, 20L)]
    [InlineData("fixture-journal", 0L, 11L, 20L)]
    [InlineData("fixture-journal", 0L, 0L, 9L)]
    public async Task JournalIdentityAndEitherRangeBoundaryGapRequireReconciliation(
        string journal, long first, long lowest, long next)
    {
        var seed = await Sample();
        var state = seed.Start().Apply(new(seed.Root.RootIdentity, 1, InventoryNoticeKind.Changed,
            new(journal, first, lowest, next)));
        Assert.True(state.Issues.HasFlag(InventoryCoverageIssues.JournalGap));
        Assert.Equal(seed.Cursor, state.Checkpoint);
        await AssertRetained(seed);
    }

    [Fact]
    public async Task IndependentValidLowerBoundsDoNotInventAJournalGap()
    {
        var seed = await Sample();
        var state = seed.Start().Apply(new(seed.Root.RootIdentity, 1, InventoryNoticeKind.Changed,
            new("fixture-journal", 8, 5, 20)));
        Assert.False(state.Issues.HasFlag(InventoryCoverageIssues.JournalGap));
        Assert.Equal(InventoryCoverage.Stale, state.Coverage);
    }

    [Fact]
    public async Task UnavailableSourceIsOfflineAndNeverAnEmptyOrDeletedInventory()
    {
        var seed = await Sample();
        var state = seed.Start().Apply(new(seed.Root.RootIdentity, 1, InventoryNoticeKind.SourceUnavailable));
        state = state.Apply(new(seed.Root.RootIdentity, 2, InventoryNoticeKind.Changed));
        Assert.Equal(InventoryCoverage.Offline, state.Coverage);
        Assert.Equal(2, state.PublishedRoot.EntryCount);
        Assert.Equal(seed.Cursor, state.Checkpoint);
        await AssertRetained(seed);
    }

    [Fact]
    public async Task CancellationHasPriorityAndCannotPartiallyApplyAStateTransition()
    {
        var seed = await Sample();
        var state = seed.Start(); var before = state.Export();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => state.Apply(
            new(seed.Root.RootIdentity, 1, (InventoryNoticeKind)99), cancellation.Token));
        Assert.Equal(before, state.Export());
        await AssertRetained(seed);
    }

    [Fact]
    public async Task IngestionCountsAndObservedJournalHighWaterCannotInventACommittedCheckpoint()
    {
        var seed = await Sample();
        var state = InventoryCoverageState.Create(seed.Root, seed.Scan).Apply(new(seed.Root.RootIdentity, 1,
            InventoryNoticeKind.Changed, new("fixture-journal", 0, 0, 999999)));
        Assert.Null(state.Checkpoint);
        Assert.True(state.Issues.HasFlag(InventoryCoverageIssues.UnboundCheckpoint));
        Assert.True(state.RequiresReconciliation);
        await AssertRetained(seed);
    }

    [Fact]
    public async Task RestartReceiptRoundTripsThroughTheExistingOwnedFixtureControlFile()
    {
        var seed = await Sample();
        var current = seed.Start().Apply(new(seed.Root.RootIdentity, 8, InventoryNoticeKind.WatcherOverflow));
        using var fixture = FixtureWorkspace.Create();
        fixture.WriteControl("receipt.json", JsonSerializer.SerializeToUtf8Bytes(current.Export()));
        var receipt = JsonSerializer.Deserialize<InventoryCoverageReceipt>(fixture.ReadControlBytes("receipt.json"))
            ?? throw new InvalidOperationException("fixture.coverage_receipt_missing");
        var restored = InventoryCoverageState.Restore(seed.Root, seed.Scan, seed.Cursor, receipt, current.Floor);
        Assert.Equal(current.Export(), restored.Export());
        Assert.Equal(seed.Cursor, restored.Checkpoint);
        await AssertRetained(seed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RollbackOfNoticeWatermarkOrRequiredUncertaintyIsRefused(bool olderSequence)
    {
        var seed = await Sample();
        var current = seed.Start().Apply(new(seed.Root.RootIdentity, 9, InventoryNoticeKind.SourceUnavailable));
        var receipt = current.Export();
        var rolledBack = olderSequence ? receipt with { LastNoticeSequence = 1 }
            : receipt with { Issues = InventoryCoverageIssues.ChangeNotice };
        Assert.Equal("inventory.coverage_rollback_refused", Assert.Throws<InventoryIndexException>(() =>
            InventoryCoverageState.Restore(seed.Root, seed.Scan, seed.Cursor, rolledBack, current.Floor)).Code);
        await AssertRetained(seed);
    }

    [Theory]
    [InlineData("ahead")]
    [InlineData("older")]
    [InlineData("journal")]
    [InlineData("generation")]
    public async Task RestartCannotSubstituteAForwardOrRolledBackCheckpointOrPublication(string fault)
    {
        var seed = await Sample();
        var state = seed.Start().Apply(new(seed.Root.RootIdentity, 1, InventoryNoticeKind.Changed));
        var receipt = state.Export();
        var altered = fault switch
        {
            "ahead" => receipt with { Checkpoint = seed.Cursor with { NextUsn = 11 } },
            "older" => receipt with { Checkpoint = seed.Cursor with { NextUsn = 9 } },
            "journal" => receipt with { Checkpoint = seed.Cursor with { JournalIdentity = "other" } },
            _ => receipt with { GenerationId = Guid.NewGuid().ToString("N") },
        };
        Assert.Equal("inventory.coverage_invalid_receipt", Assert.Throws<InventoryIndexException>(() =>
            InventoryCoverageState.Restore(seed.Root, seed.Scan, seed.Cursor, altered, state.Floor)).Code);
        await AssertRetained(seed);
    }

    [Fact]
    public async Task WrongRootInvalidRangeAndUnpublishedReferenceRefuseWithoutMutation()
    {
        var seed = await Sample(); var state = seed.Start(); var before = state.Export();
        Assert.Equal("inventory.coverage_wrong_root", Assert.Throws<InventoryIndexException>(() =>
            state.Apply(new("wrong-root", 1, InventoryNoticeKind.Changed))).Code);
        Assert.Equal("inventory.coverage_invalid_journal_range", Assert.Throws<InventoryIndexException>(() =>
            state.Apply(new(seed.Root.RootIdentity, 1, InventoryNoticeKind.Changed, new("fixture-journal", 21, 0, 20)))).Code);
        Assert.Equal("inventory.coverage_invalid_publication", Assert.Throws<InventoryIndexException>(() =>
            InventoryCoverageState.Create(seed.Root, seed.Scan with { Status = InventoryRunStatus.Incomplete }, seed.Cursor)).Code);
        Assert.Equal(before, state.Export());
        await AssertRetained(seed);
    }

    [Fact]
    public async Task RestartCannotUseAnotherRootsFloorOrUnknownUncertaintyBits()
    {
        var seed = await Sample(); var state = seed.Start();
        Assert.Equal("inventory.coverage_invalid_receipt", Assert.Throws<InventoryIndexException>(() =>
            InventoryCoverageState.Restore(seed.Root, seed.Scan, seed.Cursor, state.Export(),
                state.Floor with { RootIdentity = "different-root" })).Code);
        Assert.Equal("inventory.coverage_invalid_receipt", Assert.Throws<InventoryIndexException>(() =>
            InventoryCoverageState.Restore(seed.Root, seed.Scan, seed.Cursor,
                state.Export() with { Issues = (InventoryCoverageIssues)128 }, state.Floor)).Code);
        await AssertRetained(seed);
    }

    private sealed record Seed(AppDataPaths Paths, InventoryRootState Root, InventoryScanState Scan,
        InventoryCommittedCheckpoint Cursor)
    {
        public InventoryCoverageState Start() => InventoryCoverageState.Create(Root, Scan, Cursor);
    }

    private static async Task<Seed> Sample()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var epoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var result = await new SqliteInventoryStore(paths).IndexAsync(new("coverage-fixture-root",
            [new("a.txt", InventoryEntryKind.File, "object-a", 7, epoch, 128),
             new("b.txt", InventoryEntryKind.File, "object-b", 9, epoch, 128)]), 1, CancellationToken.None);
        Assert.True(result.Published);
        var state = await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None);
        var root = Assert.Single(state.Roots); var scan = Assert.Single(state.Runs);
        // Declared fixture metadata, not a measured USN or a checkpoint emitted by FO-005.
        var cursor = new InventoryCommittedCheckpoint(root.RootIdentity, root.GenerationId, scan.Fingerprint,
            "fixture-journal", 10);
        return new(paths, root, scan, cursor);
    }

    private static async Task AssertRetained(Seed seed)
    {
        var state = await new SqliteInventoryStore(seed.Paths).ReadStateAsync(CancellationToken.None);
        Assert.Equal(seed.Root, Assert.Single(state.Roots));
        Assert.Equal(seed.Scan, Assert.Single(state.Runs));
        var page = await new SqliteIndexedInventoryReader(seed.Paths).ReadPageAsync(
            new(seed.Root.RootIdentity, seed.Root.GenerationId), CancellationToken.None);
        Assert.Equal(new[] { "a.txt", "b.txt" }, page.Rows.Select(row => row.RelativePath));
    }
}
