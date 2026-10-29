using FileOrganizer.Application;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class LifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ReadyWorkerStopsWithoutReinitializingStore()
    {
        var store = new ImmediateStore();
        using var worker = new BootstrapWorker(store, NullLogger<BootstrapWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        Assert.Equal(new StoreState(1, 1), await worker.Readiness.WaitAsync(Deadline));
        using var stop = new CancellationTokenSource(Deadline);
        await worker.StopAsync(stop.Token);
        await worker.Completion.WaitAsync(Deadline);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task StoppingDuringInitializationCancelsReadinessAndReleasesWorker()
    {
        var store = new BlockingStore();
        using var worker = new BootstrapWorker(store, NullLogger<BootstrapWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        await store.Entered.Task.WaitAsync(Deadline);
        using var stop = new CancellationTokenSource(Deadline);
        await worker.StopAsync(stop.Token);
        await worker.Completion.WaitAsync(Deadline);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.Readiness);
        Assert.True(store.CancellationObserved);
    }

    [Fact]
    public void ProfileCannotBeOpenedTwiceUntilLeaseIsReleased()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        using (var first = ProfileLease.Acquire(paths))
        {
            Assert.ThrowsAny<IOException>(() => ProfileLease.Acquire(paths));
        }

        using var reopened = ProfileLease.Acquire(paths);
        Assert.NotNull(reopened);
    }

    [Fact]
    public void DefaultPathsUsePerUserLocalApplicationDataWithoutCreatingIt()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = AppDataPaths.CreateDefault();
        Assert.Equal(Path.GetFullPath(Path.Combine(local, "Limited Underground", "File Organizer")), paths.RootDirectory);
        Assert.Equal(Path.Combine(paths.RootDirectory, "data", "inventory.db"), paths.DatabasePath);
    }

    private sealed class ImmediateStore : IApplicationStore
    {
        public int Calls { get; private set; }

        public Task<StoreState> InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(new StoreState(1, 1));
        }
    }

    private sealed class BlockingStore : IApplicationStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public async Task<StoreState> InitializeAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }

            throw new InvalidOperationException("The blocked fixture must never initialize successfully.");
        }
    }
}
