using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FileOrganizer.Application;

/// <summary>Initializes the store off the UI thread, then waits without inventing background work.</summary>
public sealed class BootstrapWorker(
    IApplicationStore store,
    ILogger<BootstrapWorker> logger) : BackgroundService
{
    private readonly TaskCompletionSource<StoreState> readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<StoreState> Readiness => readiness.Task;
    public Task Completion => completion.Task;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var state = await Task.Run(
                () => store.InitializeAsync(stoppingToken), stoppingToken).ConfigureAwait(false);
            readiness.TrySetResult(state);
            logger.LogInformation(new EventId(1001), "Store initialization completed.");
            await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            readiness.TrySetCanceled(stoppingToken);
            logger.LogInformation(new EventId(1002), "Bootstrap worker stopped.");
        }
        catch (Exception exception)
        {
            readiness.TrySetException(exception);
            completion.TrySetException(exception);
            logger.LogError(new EventId(1003), exception, "Store initialization failed.");
            throw;
        }
        finally
        {
            completion.TrySetResult();
        }
    }
}
