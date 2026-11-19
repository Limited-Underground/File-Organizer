using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;

namespace FileOrganizer.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--fixture-operation-child")
            return await OperationProcessRecoveryTests.RunChildAsync(args[1], args[2]);
        if (args.Length == 3 && args[0] == "--fixture-reconciliation-child")
            return await T0057InventoryReconciliationTests.RunChildAsync(args[1], args[2]);
        if (args.Length == 5 && args[0] == "--fixture-capture-child") return await RunCaptureChild(args);
        if (args.Length != 3 || args[0] != "--fixture-index-child" || args[2] is not ("pause" or "resume")) return 64;
        try
        {
            using var session = ProcessInventorySession.OpenChild(args[1]);
            using var lease = ProfileLease.Acquire(session.Paths);
            session.ValidateProfileBoundary();
            var store = new SqliteInventoryStore(session.Paths);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            IProgress<InventoryIndexResult>? progress = args[2] == "pause" ? new PauseAfterDurableBatch(timeout.Token) : null;
            var result = await store.IndexAsync(session.Snapshot, 2, timeout.Token, progress);
            session.WriteReceipt("resume-result.json", JsonSerializer.SerializeToUtf8Bytes(result));
            Console.Out.WriteLine(JsonSerializer.Serialize(result));
            Console.Out.Flush();
            return result.Published && result.Status == InventoryRunStatus.Completed ? 0 : 65;
        }
        catch (Exception exception)
        {
            // Child diagnostics contain a safe type only, never paths, SQL, or snapshot contents.
            Console.Error.WriteLine($"fixture.child.failed:{exception.GetType().Name}");
            Console.Error.Flush();
            return 66;
        }
    }

    private static async Task<int> RunCaptureChild(string[] args)
    {
        if (args[3] is not ("pause" or "resume") ||
            (args[4] != "new" && (!Guid.TryParseExact(args[4], "N", out var generation) || generation.ToString("N") != args[4]))) return 64;
        try
        {
            using var session = ProcessInventorySession.OpenChild(args[1], "FO-005-streaming-20261029");
            using var lease = ProfileLease.Acquire(session.Paths);
            session.ValidateProfileBoundary();
            var source = FixtureStreamingSource.FromId(args[2], session.Snapshot.RootIdentity);
            // Process startup/restart is included in the persisted capture budget. This is
            // a bounded recovery correctness test, not a ten-second throughput assertion.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            IProgress<InventoryCaptureProgress>? progress = args[3] == "pause" ? new PauseCapture(deadline.Token) : null;
            var result = await new SqliteInventoryStore(session.Paths).CaptureAsync(source,
                new InventoryCaptureOptions { PageSize = 7, MaxEntries = 5000, MaxDirectories = 100, MaxDuration = TimeSpan.FromSeconds(60) },
                args[4] == "new" ? null : args[4], deadline.Token, progress);
            session.WriteReceipt("capture-resumed.json", JsonSerializer.SerializeToUtf8Bytes(result));
            Console.Out.WriteLine(JsonSerializer.Serialize(result));
            Console.Out.Flush();
            return result.Published ? 0 : 65;
        }
        catch (Exception exception)
        {
            var diagnostic = exception is InventoryIndexException index ? index.Code : exception.GetType().Name;
            Console.Error.WriteLine($"fixture.capture_child.failed:{diagnostic}");
            Console.Error.Flush();
            return 66;
        }
    }

    private sealed class PauseCapture(CancellationToken deadline) : IProgress<InventoryCaptureProgress>
    {
        private bool paused;
        public void Report(InventoryCaptureProgress progress)
        {
            if (paused || progress.Phase != InventoryCapturePhase.Capturing || progress.CapturedEntries < 7) return;
            paused = true;
            Console.Out.WriteLine(JsonSerializer.Serialize(progress));
            Console.Out.Flush();
            deadline.WaitHandle.WaitOne();
            deadline.ThrowIfCancellationRequested();
        }
    }

    private sealed class PauseAfterDurableBatch(CancellationToken timeout) : IProgress<InventoryIndexResult>
    {
        public void Report(InventoryIndexResult value)
        {
            if (value.Status != InventoryRunStatus.Running || value.CommittedEntries != 2) return;
            // Report is synchronous and runs strictly after the store's transaction commit.
            Console.Out.WriteLine(JsonSerializer.Serialize(value));
            Console.Out.Flush();
            // The child's own deadline also prevents an orphan if the parent disappears.
            timeout.WaitHandle.WaitOne();
            timeout.ThrowIfCancellationRequested();
        }
    }
}
