using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class InventoryContinuityTests
{
    private static readonly DateTime Observed = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ActualFileChangeDuringIngestionCannotReplacePreviousGeneration()
    {
        using var fixture = FixtureWorkspace.Create(evidenceTask: "FO-005-recovery-20261029");
        await new FixtureGenerator().GenerateAsync(fixture, new(23, [new("protected.bin", 13, "protected")]));
        var protectedBytes = fixture.ReadOwnedBytes("protected.bin");
        var source = new FixtureInventorySource(fixture);
        var paths = new AppDataPaths(new TestWorkspace().Root);
        var store = new SqliteInventoryStore(paths);
        var old = await new InventoryIndexer(source, store).RunAsync();
        // Only this explicit fault actor writes a new, unplanned disposable file.
        var changedFile = Path.Combine(fixture.Root, "unplanned.bin");
        File.WriteAllBytes(changedFile, [1]);
        var changed = false;
        var progress = new InlineProgress(value =>
        {
            if (!changed && value.Status == InventoryRunStatus.Running)
            {
                File.WriteAllBytes(changedFile, [1, 2, 3, 4]);
                changed = true;
            }
        });
        var incomplete = await new InventoryIndexer(source, store).RunAsync(1, progress: progress);
        Assert.True(changed);
        Assert.Equal(InventoryRunStatus.Incomplete, incomplete.Status);
        Assert.False(incomplete.Published);
        Assert.False(incomplete.IsAtomicSnapshot);
        var state = await store.ReadStateAsync(CancellationToken.None);
        Assert.Equal(old.GenerationId, Assert.Single(state.Roots).GenerationId);
        Assert.False(Assert.Single(state.Roots).IsAtomicSnapshot);
        Assert.Equal(protectedBytes, fixture.ReadOwnedBytes("protected.bin"));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(changedFile));
        var stable = await new InventoryIndexer(source, store).RunAsync(1);
        Assert.True(stable.Published);
        Assert.NotEqual(incomplete.GenerationId, stable.GenerationId);
        Assert.Equal(protectedBytes, fixture.ReadOwnedBytes("protected.bin"));
    }

    [Fact]
    public async Task FailedRevalidationOfExistingCompletedGenerationPreservesHistoricalPublication()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        var store = new SqliteInventoryStore(paths);
        var first = Snapshot(1);
        var old = await store.IndexAsync(first, 1, CancellationToken.None);
        var source = new SequenceSource(first, Snapshot(2));
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() => new InventoryIndexer(source, store).RunAsync());
        Assert.Equal("inventory.source_changed", error.Code);
        var state = await store.ReadStateAsync(CancellationToken.None);
        Assert.Equal(old.GenerationId, Assert.Single(state.Roots).GenerationId);
        Assert.Equal(InventoryRunStatus.Completed, Assert.Single(state.Runs).Status);
    }

    [Fact]
    public async Task RecaptureFailureRetainsStagingWithoutPublishingAndCanRetrySameFingerprint()
    {
        var store = new SqliteInventoryStore(new AppDataPaths(new TestWorkspace().Root));
        var first = Snapshot(1);
        var result = await new InventoryIndexer(new FailingRecaptureSource(first), store).RunAsync(1);
        Assert.Equal(InventoryRunStatus.Incomplete, result.Status);
        Assert.Empty((await store.ReadStateAsync(CancellationToken.None)).Roots);
        var retry = await new InventoryIndexer(new SequenceSource(first, first), store).RunAsync(1);
        Assert.Equal(result.GenerationId, retry.GenerationId);
        Assert.True(retry.Published);
    }

    [Fact]
    public async Task CancellationDuringRecapturePreservesCommittedCursorAndPriorGeneration()
    {
        var store = new SqliteInventoryStore(new AppDataPaths(new TestWorkspace().Root));
        var old = await store.IndexAsync(Snapshot(1), 1, CancellationToken.None);
        using var cancel = new CancellationTokenSource();
        var next = Snapshot(2);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.IndexAsync(next, 1, cancel.Token,
            recapture: token => { cancel.Cancel(); token.ThrowIfCancellationRequested(); return Task.FromResult(next); }));
        var state = await store.ReadStateAsync(CancellationToken.None);
        Assert.Equal(old.GenerationId, Assert.Single(state.Roots).GenerationId);
        var cancelled = Assert.Single(state.Runs, run => run.Status == InventoryRunStatus.Cancelled);
        Assert.Equal(1, cancelled.CommittedEntries);
        var resumed = await new InventoryIndexer(new SequenceSource(next, next), store).RunAsync(1);
        Assert.Equal(cancelled.GenerationId, resumed.GenerationId);
        Assert.True(resumed.Published);
    }

    private static InventorySnapshot Snapshot(long length) => new("continuity-root",
        [new("file.bin", InventoryEntryKind.File, "file-id", length, Observed, 128)]);

    private sealed class InlineProgress(Action<InventoryIndexResult> action) : IProgress<InventoryIndexResult>
    {
        public void Report(InventoryIndexResult value) => action(value);
    }

    private sealed class SequenceSource(InventorySnapshot initial, InventorySnapshot final) : IInventorySource
    {
        private int captures;
        public Task<InventorySnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Interlocked.Increment(ref captures) == 1 ? initial : final);
        }
    }

    private sealed class FailingRecaptureSource(InventorySnapshot initial) : IInventorySource
    {
        private int captures;
        public Task<InventorySnapshot> CaptureAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref captures) > 1) throw new IOException("disposable fixture capture failure");
            return Task.FromResult(initial);
        }
    }
}
