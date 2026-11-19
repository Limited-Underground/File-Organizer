using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Xunit;

namespace FileOrganizer.Tests;

[CollectionDefinition("Bounded inventory timing", DisableParallelization = true)]
public sealed class InventoryTimingCollection { }

[Collection("Bounded inventory timing")]
public sealed class InventoryPerformanceTests
{
    [Fact]
    public async Task MeasureBoundedNativeCaptureAndDurableIndexingWithoutChangingInputs()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var samples = new List<object>();
        string? evidence = null;
        foreach (var count in new[] { 32, 128, 256 })
        {
            using var fixture = FixtureWorkspace.Create(new FixtureLimits { MaxFiles = 256 }, "FO-005-recovery-20261029");
            var specs = Enumerable.Range(0, count).Select(index => new FixtureFileSpec($"file-{index:D4}.bin", 32, $"key-{index}")).ToArray();
            await new FixtureGenerator().GenerateAsync(fixture, new(1259, specs), cancellationToken: budget.Token);
            var hashes = specs.ToDictionary(spec => spec.RelativePath,
                spec => Convert.ToHexString(SHA256.HashData(fixture.ReadOwnedBytes(spec.RelativePath))));
            var source = new FixtureInventorySource(fixture);
            foreach (var batch in new[] { 1, 16, 64 })
            {
                // One warmup and three measured, separate disposable SQLite profiles per scenario.
                for (var iteration = 0; iteration < 4; iteration++)
                {
                    budget.Token.ThrowIfCancellationRequested();
                    var timer = Stopwatch.StartNew();
                    var captured = await source.CaptureAsync(budget.Token);
                    var captureMs = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    var prepared = PreparedInventorySnapshot.Create(captured);
                    var prepareMs = timer.Elapsed.TotalMilliseconds;
                    Assert.Equal(count, prepared.Entries.Count);
                    var workspace = new TestWorkspace();
                    evidence = Directory.GetParent(Directory.GetParent(workspace.Root)!.FullName)!.FullName;
                    var paths = new AppDataPaths(workspace.Root);
                    using var lease = ProfileLease.Acquire(paths);
                    timer.Restart();
                    await new SqliteApplicationStore(paths).InitializeAsync(budget.Token);
                    var initializeMs = timer.Elapsed.TotalMilliseconds;
                    timer.Restart();
                    var result = await new InventoryIndexer(source, new SqliteInventoryStore(paths)).RunAsync(batch, budget.Token);
                    var ingestionMs = timer.Elapsed.TotalMilliseconds;
                    Assert.True(result.Published);
                    Assert.False(result.IsAtomicSnapshot);
                    Assert.Equal(count, result.CommittedEntries);
                    timer.Restart();
                    var state = await new SqliteInventoryStore(paths).ReadStateAsync(budget.Token);
                    var reopenMs = timer.Elapsed.TotalMilliseconds;
                    Assert.Equal(count, Assert.Single(state.Roots).EntryCount);
                    if (iteration > 0)
                    {
                        samples.Add(new
                        {
                            Count = count, BatchSize = batch, Iteration = iteration,
                            CaptureMs = captureMs, PrepareMs = prepareMs, SchemaOneInitializationMs = initializeMs,
                            IndexIncludingMigrationAndRecaptureMs = ingestionMs, ReopenReadMs = reopenMs,
                            DatabaseBytes = new FileInfo(paths.DatabasePath).Length,
                            ProcessWorkingSetBytes = Environment.WorkingSet,
                            SourceBytes = count * 32,
                        });
                    }
                }
            }
            foreach (var spec in specs)
            {
                Assert.Equal(hashes[spec.RelativePath], Convert.ToHexString(SHA256.HashData(fixture.ReadOwnedBytes(spec.RelativePath))));
                Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                    File.GetLastWriteTimeUtc(Path.Combine(fixture.Root, spec.RelativePath)));
            }
        }
        Assert.Equal(27, samples.Count);
        var configuration = typeof(InventoryPerformanceTests).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var path = Path.Combine(evidence!, $"performance-{configuration}-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            Configuration = configuration,
            Scope = "Bounded fixture measurements only; not a million-record forecast or latency qualification. Shared host contention and process memory include runtime effects.",
            WarmupsPerScenario = 1, MeasuredRunsPerScenario = 3, Samples = samples,
        }, new JsonSerializerOptions { WriteIndented = true }), budget.Token);
    }
}
