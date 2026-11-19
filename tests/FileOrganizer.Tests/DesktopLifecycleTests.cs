using FileOrganizer.Application;
using FileOrganizer.Desktop;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class DesktopLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task NeverStartedRuntimeDisposesAndReleasesProfile()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        var runtime = new DesktopRuntime(paths);
        await runtime.DisposeAsync().AsTask().WaitAsync(Deadline);
        using var reopened = ProfileLease.Acquire(paths);
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task InitializationFailurePropagatesAndDisposalReleasesProfile()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        var runtime = new DesktopRuntime(paths, new FailingStore());
        using var deadline = new CancellationTokenSource(Deadline);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.StartAsync(deadline.Token));
        Assert.Equal("fixture startup failure", failure.Message);
        // Disposal may surface the same failed background task. Either way the profile must release.
        await Record.ExceptionAsync(() => runtime.DisposeAsync().AsTask().WaitAsync(Deadline));
        using var reopened = ProfileLease.Acquire(paths);
        Assert.NotNull(reopened);
    }

    [Fact]
    public async Task TimedOutShutdownKeepsProfileExclusiveUntilWorkerActuallyCompletes()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        var store = new NoncooperativeStore();
        var runtime = new DesktopRuntime(paths, store, TimeSpan.FromMilliseconds(100));
        using var deadline = new CancellationTokenSource(Deadline);
        var startup = runtime.StartAsync(deadline.Token);
        await store.Entered.Task.WaitAsync(Deadline);
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runtime.DisposeAsync().AsTask());
            Assert.ThrowsAny<IOException>(() => ProfileLease.Acquire(paths));
        }
        finally
        {
            store.Release.TrySetResult();
        }

        await startup.WaitAsync(Deadline);
        await runtime.Worker.Completion.WaitAsync(Deadline);
        // The completion continuation releases ownership; allow it to run without assuming scheduler order.
        using var reopened = await AcquireEventually(paths, deadline.Token);
        Assert.NotNull(reopened);
    }

    private static async Task<ProfileLease> AcquireEventually(AppDataPaths paths, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                return ProfileLease.Acquire(paths);
            }
            catch (IOException)
            {
                await Task.Delay(10, token);
            }
        }
    }

    private sealed class FailingStore : IApplicationStore
    {
        public Task<StoreState> InitializeAsync(CancellationToken cancellationToken) =>
            Task.FromException<StoreState>(new InvalidOperationException("fixture startup failure"));
    }

    private sealed class NoncooperativeStore : IApplicationStore
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<StoreState> InitializeAsync(CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Release.Task;
            return new StoreState(1, 1);
        }
    }
}
