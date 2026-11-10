using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class NativeInitialScanFactAttribute : FactAttribute
{
    public NativeInitialScanFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FO_RUN_NATIVE") != "1")
            Skip = "Explicit opt-in native 20,000-file/1-GiB initial-scan benchmark; not run in ordinary regressions.";
    }
}

public sealed class InventoryNativeBenchmark
{
    [NativeInitialScanFact]
    public async Task TwentyThousandGeneratedFilesStreamVerifyAndResumeWithoutSourceMutation()
    {
        Assert.Equal("Release", typeof(InventoryNativeBenchmark).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration);
        Assert.False(Debugger.IsAttached);
        const long gib = 1024L * 1024 * 1024;
        var checkout = ProcessInventorySession.FindCheckout();
        var drive = new DriveInfo(Path.GetPathRoot(checkout)!);
        Assert.True(drive.AvailableFreeSpace >= 13 * gib + 64L * 1024 * 1024, "Insufficient admitted free space including payload and metadata reserve; no payload created.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        var total = Stopwatch.StartNew();
        using var fixture = await StreamingFixture.CreateAsync(new()
        { FileCount = 20_000, DirectoryCount = 100, TotalBytes = gib, MinimumFreeBytes = 12 * gib }, deadline.Token);
        var generationMs = total.Elapsed.TotalMilliseconds;
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), "FO-005-streaming-20261029");
        using var lease = ProfileLease.Acquire(session.Paths);
        var source = new FixtureStreamingSource(fixture);
        var options = new InventoryCaptureOptions { PageSize = 1000, MaxEntries = 25_000, MaxDirectories = 256, MaxDuration = TimeSpan.FromMinutes(45) };
        var progress = new NativeProgress(fixture, session.Paths, gib, drive, deadline.Token);
        progress.Check();
        var timer = Stopwatch.StartNew();
        var store = new SqliteInventoryStore(session.Paths);
        var result = await store.CaptureAsync(source, options, null, deadline.Token, progress);
        var captureMs = timer.Elapsed.TotalMilliseconds;
        Assert.True(result.Published);
        Assert.False(result.IsAtomicSnapshot);
        Assert.Equal(20_100, result.CommittedEntries);
        timer.Restart();
        var verified = await fixture.VerifyUnchangedAsync(deadline.Token);
        var verifyMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(20_000, verified.FileCount);
        Assert.Equal(gib, verified.TotalBytes);
        await VerifyStoredOracle(fixture, session.Paths, result.GenerationId, deadline.Token);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var cancellationClock = new Stopwatch();
        string? cancelledGeneration = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CaptureAsync(source, options, null, cancellation.Token,
            new CaptureProgress(value =>
            {
                progress.Report(value);
                if (value.Phase == InventoryCapturePhase.Capturing && value.CapturedEntries >= 1000 && !cancellation.IsCancellationRequested)
                { cancelledGeneration = value.GenerationId; cancellationClock.Start(); cancellation.Cancel(); }
            })));
        var cancellationMs = cancellationClock.Elapsed.TotalMilliseconds;
        var state = await store.ReadStateAsync(deadline.Token);
        Assert.Equal(result.GenerationId, Assert.Single(state.Roots).GenerationId);
        var cancelled = Assert.Single(state.Runs, run => run.GenerationId == cancelledGeneration);
        Assert.Equal(InventoryRunStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CommittedEntries >= 1000);
        timer.Restart();
        var resumed = await store.CaptureAsync(source, options, cancelledGeneration, deadline.Token, progress);
        var resumeMs = timer.Elapsed.TotalMilliseconds;
        Assert.Equal(cancelledGeneration, resumed.GenerationId);
        Assert.True(resumed.Published);
        Assert.Equal(20_100, resumed.CommittedEntries);
        await VerifyStoredOracle(fixture, session.Paths, resumed.GenerationId, deadline.Token);
        var finalSource = await fixture.VerifyUnchangedAsync(deadline.Token);
        progress.Check();
        var receipt = new
        {
            Scenario = "B02 initial native scan only; no incremental mutations or transfer operations",
            Configuration = "Release", Files = 20_000, Directories = 100, Bytes = gib,
            GeneratedMilliseconds = generationMs, CapturedAndVerifiedMilliseconds = captureMs,
            EntriesPerSecond = 20_100 * 1000d / captureMs, SourceShaVerificationMilliseconds = verifyMs,
            CancellationMilliseconds = cancellationMs, ResumeMilliseconds = resumeMs,
            cancelled.CommittedEntries, result.GenerationId, ResumedGeneration = resumed.GenerationId,
            PeakProcessRssBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            DatabaseBytes = new FileInfo(session.Paths.DatabasePath).Length,
            SourceOracle = finalSource, ElapsedMilliseconds = total.Elapsed.TotalMilliseconds,
            IsAtomicSnapshot = false, NoSourceMutation = true,
        };
        session.WriteReceipt("native-benchmark.json", JsonSerializer.SerializeToUtf8Bytes(receipt));
        Console.WriteLine(JsonSerializer.Serialize(receipt));
    }

    private static async Task VerifyStoredOracle(StreamingFixture fixture, AppDataPaths paths, string generation, CancellationToken token)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Kind,ObjectIdentity,Length,ModifiedUtc,Attributes,ErrorCode FROM InventoryDirectoryEntry WHERE GenerationId=$g AND RelativePath=$p;";
        command.Parameters.AddWithValue("$g", generation);
        var parameter = command.Parameters.AddWithValue("$p", string.Empty);
        var count = 0;
        await foreach (var expected in fixture.ReadOracleAsync(token))
        {
            token.ThrowIfCancellationRequested();
            parameter.Value = expected.Observation.RelativePath;
            using var reader = command.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal((int)expected.Observation.Kind, reader.GetInt32(0));
            Assert.Equal(expected.Observation.ObjectIdentity, reader.GetString(1));
            Assert.Equal(expected.Observation.Length, reader.GetInt64(2));
            Assert.Equal(expected.Observation.ModifiedUtc.ToString("O", CultureInfo.InvariantCulture), reader.GetString(3));
            Assert.Equal(expected.Observation.Attributes, reader.GetInt64(4));
            Assert.True(reader.IsDBNull(5));
            Assert.False(reader.Read());
            count++;
        }
        Assert.Equal(20_100, count);
    }

    private sealed class CaptureProgress(Action<InventoryCaptureProgress> action) : IProgress<InventoryCaptureProgress>
    { public void Report(InventoryCaptureProgress value) => action(value); }

    private sealed class NativeProgress(StreamingFixture fixture, AppDataPaths paths, long sourceBytes, DriveInfo drive, CancellationToken token) : IProgress<InventoryCaptureProgress>
    {
        public void Report(InventoryCaptureProgress value) => Check();
        public void Check()
        {
            token.ThrowIfCancellationRequested();
            const long gib = 1024L * 1024 * 1024;
            Assert.True(drive.AvailableFreeSpace >= 12 * gib);
            Assert.True(Process.GetCurrentProcess().WorkingSet64 <= gib);
            Assert.True(Process.GetCurrentProcess().PeakWorkingSet64 <= gib);
            var total = sourceBytes + new FileInfo(fixture.OraclePath).Length;
            if (Directory.Exists(paths.RootDirectory))
                foreach (var file in Directory.EnumerateFiles(paths.RootDirectory, "*", new EnumerationOptions
                { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var length = new FileInfo(file).Length;
                    if (file.EndsWith("-wal", StringComparison.Ordinal)) Assert.True(length <= gib / 2);
                    else if (file.EndsWith(".db", StringComparison.Ordinal)) Assert.True(length <= 2 * gib);
                    total += length;
                }
            Assert.True(total <= 6 * gib);
        }
    }
}
