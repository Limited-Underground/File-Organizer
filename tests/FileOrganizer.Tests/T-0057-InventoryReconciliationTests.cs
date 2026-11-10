// T-0057: actual SQLite transactions and privately minted child-process restart fixtures.
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class T0057InventoryReconciliationTests
{
    private static readonly TimeSpan ChildDeadline = TimeSpan.FromSeconds(20);
    private static InventorySnapshot BeforeTruth() => Truth(false);
    private static InventorySnapshot AfterTruth() => Truth(true);
    private static InventorySnapshot Truth(bool after)
    {
        var epoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        return new("reconcile-fixture", [new("a.txt", InventoryEntryKind.File, "object-a", after ? 11 : 7, epoch, 128),
            new(after ? "new.txt" : "keep.txt", InventoryEntryKind.File, after ? "object-new" : "object-keep", 9, epoch, 128)]);
    }

    private sealed record Seed(AppDataPaths Paths, InventoryReconciliationState Pair);
    private static async Task<Seed> Sample(AppDataPaths? supplied = null)
    {
        var paths = supplied ?? new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(paths);
        await store.IndexAsync(BeforeTruth(), 1, CancellationToken.None);
        var state = await store.ReadStateAsync(CancellationToken.None);
        var root = Assert.Single(state.Roots); var scan = Assert.Single(state.Runs);
        var before = InventoryCoverageState.Create(root, scan).Apply(new(root.RootIdentity, 1, InventoryNoticeKind.Changed));
        var plan = new InventoryReconciliationPlan(Guid.NewGuid().ToString("N"), before.Export(),
            "fixture-journal", 10, SuppliedInventoryCoverage.Complete);
        var pair = await new InventoryReconciler(new SuppliedSource(BeforeTruth()), store).RunAsync(plan, 1);
        return new(paths, pair);
    }
    private static InventoryReconciliationPlan Plan(Seed seed, string? operation = null)
    {
        var pair = seed.Pair;
        var before = InventoryCoverageState.Restore(pair.Root, pair.Scan, pair.Receipt.Checkpoint, pair.Receipt, pair.Floor)
            .Apply(new(pair.Root.RootIdentity, pair.Receipt.LastNoticeSequence + 1, InventoryNoticeKind.Changed));
        return new(operation ?? Guid.NewGuid().ToString("N"), before.Export(), "fixture-journal", 20, SuppliedInventoryCoverage.Complete);
    }
    private sealed class SuppliedSource(InventorySnapshot snapshot) : IInventorySource
    {
        public int Calls { get; private set; }
        public Task<InventorySnapshot> CaptureAsync(CancellationToken token)
        { token.ThrowIfCancellationRequested(); Calls++; return Task.FromResult(snapshot); }
    }
    private sealed class InjectedFailure : Exception { }

    [Fact]
    public async Task CompletedPairAndExactRetryAreDurableAndConvergeWithoutExtraGeneration()
    {
        var seed = await Sample(); var plan = Plan(seed);
        var adopted = await new InventoryReconciler(new SuppliedSource(AfterTruth()), new SqliteInventoryStore(seed.Paths)).RunAsync(plan, 1);
        Assert.Equal(20L, adopted.Receipt.Checkpoint!.NextUsn);
        Assert.Equal(adopted.Root.GenerationId, adopted.Receipt.Checkpoint.GenerationId);
        Assert.Equal(adopted.Scan.Fingerprint, adopted.Receipt.ObservationFingerprint);
        Assert.Equal(plan.Before.LastNoticeSequence, adopted.Floor.MinimumNoticeSequence);
        Assert.NotEqual(seed.Pair.Root.GenerationId, adopted.Root.GenerationId);
        Assert.Equal(new[] { "a.txt", "new.txt" }, await Paths(seed.Paths, adopted.Root.GenerationId));
        var restarted = new SqliteInventoryStore(seed.Paths);
        Assert.Equal(adopted, await restarted.ReadReconciliationAsync(adopted.Root.RootIdentity, CancellationToken.None));
        var replay = await new InventoryReconciler(new SuppliedSource(AfterTruth()), restarted).RunAsync(plan, 1);
        Assert.Equal(adopted, replay);
        Assert.Equal(2, (await restarted.ReadStateAsync(CancellationToken.None)).Runs.Count);
        Assert.Equal("ok", Scalar(seed.Paths, "PRAGMA integrity_check;"));
        Assert.Equal(0L, Scalar(seed.Paths, "SELECT count(*) FROM pragma_foreign_key_check;"));
        await AssertItemOneAnchor(seed.Paths, adopted);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task ExceptionAtEveryFinalStepRollsBackBothAndReadersSeeOnlyPriorPair(int stage)
    {
        var seed = await Sample(); var plan = Plan(seed);
        var store = new SqliteInventoryStore(seed.Paths, point =>
        {
            if ((int)point != stage) return;
            Assert.Equal(seed.Pair.Root.GenerationId, Scalar(seed.Paths, "SELECT GenerationId FROM InventoryPublishedRoot;"));
            Assert.Equal(10L, Scalar(seed.Paths, "SELECT NextUsn FROM InventoryReconciliation;"));
            throw new InjectedFailure();
        });
        await Assert.ThrowsAsync<InjectedFailure>(() => new InventoryReconciler(new SuppliedSource(AfterTruth()), store).RunAsync(plan, 1));
        Assert.Equal(seed.Pair, await new SqliteInventoryStore(seed.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
        Assert.Equal(new[] { "a.txt", "keep.txt" }, await Paths(seed.Paths, seed.Pair.Root.GenerationId));
        var retry = await new InventoryReconciler(new SuppliedSource(AfterTruth()), new SqliteInventoryStore(seed.Paths)).RunAsync(plan, 1);
        Assert.Equal(20L, retry.Receipt.Checkpoint!.NextUsn);
        Assert.Equal(2, (await new SqliteInventoryStore(seed.Paths).ReadStateAsync(CancellationToken.None)).Runs.Count);
        Assert.Equal(new[] { "a.txt", "new.txt" }, await Paths(seed.Paths, retry.Root.GenerationId));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task AbruptChildExitAtEachStepRestartsOnOldPairAndFreshChildRetryConverges(int stage)
    {
        using var session = ProcessInventorySession.Create(AfterTruth());
        Seed seed;
        using (ProfileLease.Acquire(session.Paths)) seed = await Sample(session.Paths);
        var plan = Plan(seed, session.SessionId); session.SetReconciliationPlan(plan);
        Assert.Equal(74, await RunChild(session, "fault-" + stage));
        session.ValidateProfileBoundary();
        using (ProfileLease.Acquire(session.Paths))
        {
            Assert.Equal(seed.Pair, await new SqliteInventoryStore(session.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
            Assert.Equal(new[] { "a.txt", "keep.txt" }, await Paths(session.Paths, seed.Pair.Root.GenerationId));
            Assert.Equal("ok", Scalar(session.Paths, "PRAGMA integrity_check;"));
        }
        Assert.Equal(0, await RunChild(session, "resume"));
        var adopted = await new SqliteInventoryStore(session.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None);
        Assert.NotNull(adopted); Assert.Equal(20L, adopted.Receipt.Checkpoint!.NextUsn);
        Assert.Equal(new[] { "a.txt", "new.txt" }, await Paths(session.Paths, adopted.Root.GenerationId));
        Assert.Equal(0, await RunChild(session, "resume"));
        Assert.Equal(adopted, await new SqliteInventoryStore(session.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CancellationInsideAnyPublishStepPreservesTheDurablePair(int stage)
    {
        var seed = await Sample(); using var cancellation = new CancellationTokenSource();
        var store = new SqliteInventoryStore(seed.Paths, point => { if ((int)point == stage) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InventoryReconciler(new SuppliedSource(AfterTruth()), store)
            .RunAsync(Plan(seed), 1, cancellation.Token));
        Assert.Equal(seed.Pair, await new SqliteInventoryStore(seed.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
    }

    [Theory]
    [InlineData(SuppliedInventoryCoverage.Unknown)] [InlineData(SuppliedInventoryCoverage.Offline)]
    public async Task UnknownOrOfflineCoverageNeverCapturesOrRemovesPriorObservations(SuppliedInventoryCoverage coverage)
    {
        var seed = await Sample(); var source = new SuppliedSource(AfterTruth());
        var plan = Plan(seed) with { Coverage = coverage };
        Assert.Equal("inventory.reconciliation_unverified_coverage", (await Assert.ThrowsAsync<InventoryIndexException>(() =>
            new InventoryReconciler(source, new SqliteInventoryStore(seed.Paths)).RunAsync(plan))).Code);
        Assert.Equal(0, source.Calls);
        Assert.Equal(seed.Pair, await new SqliteInventoryStore(seed.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
        Assert.Equal(new[] { "a.txt", "keep.txt" }, await Paths(seed.Paths, seed.Pair.Root.GenerationId));
    }

    [Fact]
    public async Task RollbackCheckpointChangedJournalAndReusedOperationWithDifferentBytesAreRefused()
    {
        var seed = await Sample(); var plan = Plan(seed);
        var runner = new InventoryReconciler(new SuppliedSource(AfterTruth()), new SqliteInventoryStore(seed.Paths));
        Assert.Equal("inventory.reconciliation_checkpoint_refused", (await Assert.ThrowsAsync<InventoryIndexException>(() => runner.RunAsync(plan with { NextUsn = 9 }))).Code);
        Assert.Equal("inventory.reconciliation_checkpoint_refused", (await Assert.ThrowsAsync<InventoryIndexException>(() => runner.RunAsync(plan with { JournalIdentity = "other" }))).Code);
        var adopted = await runner.RunAsync(plan);
        Assert.Equal("inventory.reconciliation_conflict", (await Assert.ThrowsAsync<InventoryIndexException>(() => runner.RunAsync(plan with { NextUsn = 21 }))).Code);
        Assert.Equal(adopted, await new SqliteInventoryStore(seed.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
    }

    [Fact]
    public async Task ChangedRecaptureAndIncompleteTruthPreservePriorPair()
    {
        var seed = await Sample(); var store = new SqliteInventoryStore(seed.Paths); var plan = Plan(seed);
        await Assert.ThrowsAsync<InventoryIndexException>(() => store.ReconcileAsync(AfterTruth(), plan, 1, CancellationToken.None,
            _ => Task.FromResult(BeforeTruth())));
        var failed = AfterTruth() with { Entries = [new("denied", InventoryEntryKind.File, "", 0,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), 0, "filesystem.access_denied")] };
        await Assert.ThrowsAsync<InventoryIndexException>(() => new InventoryReconciler(new SuppliedSource(failed), store).RunAsync(plan));
        Assert.Equal(seed.Pair, await store.ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
    }

    [Fact]
    public async Task LegacyPublicationCannotDetachAManagedCheckpointAndForeignKeyRejectsTornPair()
    {
        var seed = await Sample();
        Assert.Equal("inventory.reconciliation_required", (await Assert.ThrowsAsync<InventoryIndexException>(() =>
            new SqliteInventoryStore(seed.Paths).IndexAsync(AfterTruth(), 1, CancellationToken.None))).Code);
        var adopted = await new InventoryReconciler(new SuppliedSource(AfterTruth()), new SqliteInventoryStore(seed.Paths)).RunAsync(Plan(seed));
        using var connection = Connect(seed.Paths); using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE InventoryPublishedRoot SET GenerationId=$old;";
        command.Parameters.AddWithValue("$old", seed.Pair.Root.GenerationId); command.ExecuteNonQuery();
        Assert.Throws<SqliteException>(() => transaction.Commit()); transaction.Rollback();
        Assert.Equal(adopted, await new SqliteInventoryStore(seed.Paths).ReadReconciliationAsync(seed.Pair.Root.RootIdentity, CancellationToken.None));
    }

    [Fact]
    public async Task AdditiveMigrationRollsBackPreservesVerifiedBackupAndRefusesUnknownVersion()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        await new SqliteInventoryStore(paths).IndexAsync(BeforeTruth(), 1, CancellationToken.None);
        using (var connection = Connect(paths)) InventoryCaptureSchema.Ensure(connection, paths.DataDirectory, CancellationToken.None);
        var prior = await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None);
        var root = Assert.Single(prior.Roots); var scan = Assert.Single(prior.Runs);
        var before = InventoryCoverageState.Create(root, scan).Apply(new(root.RootIdentity, 1, InventoryNoticeKind.Changed));
        var plan = new InventoryReconciliationPlan(Guid.NewGuid().ToString("N"), before.Export(), "fixture-journal", 10, SuppliedInventoryCoverage.Complete);
        var oldChecksums = Scalar(paths, "SELECT group_concat(Checksum,',') FROM (SELECT Checksum FROM SchemaMigration ORDER BY Version);");
        var store = new SqliteInventoryStore(paths, point => { if (point == InventoryReconciliationStage.BeforeMigrationCommit) throw new InjectedFailure(); });
        await Assert.ThrowsAsync<InjectedFailure>(() => new InventoryReconciler(new SuppliedSource(AfterTruth()), store).RunAsync(plan));
        Assert.Equal(3L, Scalar(paths, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM sqlite_master WHERE name='InventoryReconciliation';"));
        Assert.Equal(oldChecksums, Scalar(paths, "SELECT group_concat(Checksum,',') FROM (SELECT Checksum FROM SchemaMigration ORDER BY Version);"));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(paths.DataDirectory, "backups"), "before-inventory-v4-*.db"));
        var hash = SHA256.HashData(File.ReadAllBytes(backup));
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
        { connection.Open(); SqliteApplicationStore.ValidateMigration(connection, null, 3); }
        var adopted = await new InventoryReconciler(new SuppliedSource(AfterTruth()), new SqliteInventoryStore(paths)).RunAsync(plan);
        Assert.Equal(5, (await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None)).SchemaVersion);
        Assert.Equal(5L, Scalar(paths, "SELECT count(*) FROM SchemaMigration;"));
        Assert.Equal(oldChecksums, Scalar(paths, "SELECT group_concat(Checksum,',') FROM (SELECT Checksum FROM SchemaMigration WHERE Version<=3 ORDER BY Version);"));
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(backup)));
        Assert.Equal(new[] { "a.txt", "new.txt" }, await Paths(paths, adopted.Root.GenerationId));
        Execute(paths, "PRAGMA user_version=6;");
        await Assert.ThrowsAsync<StoreInitializationException>(() => new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None));
    }

    private static async Task AssertItemOneAnchor(AppDataPaths paths, InventoryReconciliationState pair)
    {
        var state = InventoryCoverageState.Restore(pair.Root, pair.Scan, pair.Receipt.Checkpoint, pair.Receipt, pair.Floor);
        var offline = state.Apply(new(pair.Root.RootIdentity, pair.Floor.MinimumNoticeSequence + 1, InventoryNoticeKind.SourceUnavailable));
        Assert.Equal(InventoryCoverage.Offline, offline.Coverage);
        Assert.Equal(pair.Receipt.Checkpoint, offline.Checkpoint);
        Assert.Equal(pair, await new SqliteInventoryStore(paths).ReadReconciliationAsync(pair.Root.RootIdentity, CancellationToken.None));
    }
    private static async Task<string[]> Paths(AppDataPaths paths, string generation)
        => (await new SqliteIndexedInventoryReader(paths).ReadPageAsync(new("reconcile-fixture", generation), CancellationToken.None))
            .Rows.Select(row => row.RelativePath).ToArray();
    private static SqliteConnection Connect(AppDataPaths paths)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = "PRAGMA foreign_keys=ON;"; command.ExecuteNonQuery();
        return connection;
    }
    private static object? Scalar(AppDataPaths paths, string sql)
    { using var c = Connect(paths); using var command = c.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }
    private static void Execute(AppDataPaths paths, string sql) => _ = Scalar(paths, sql);

    private static async Task<int> RunChild(ProcessInventorySession session, string mode)
    {
        session.ValidateProfileBoundary();
        var dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = ProcessInventorySession.FindCheckout() };
        start.ArgumentList.Add(typeof(T0057InventoryReconciliationTests).Assembly.Location);
        start.ArgumentList.Add("--fixture-reconciliation-child"); start.ArgumentList.Add(session.SessionId); start.ArgumentList.Add(mode);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("fixture.child.start_failed");
        var stdout = InventoryProcessTests.ReadBoundedText(child.StandardOutput);
        var stderr = InventoryProcessTests.ReadBoundedText(child.StandardError);
        try
        {
            await child.WaitForExitAsync().WaitAsync(ChildDeadline);
            Assert.Equal(string.Empty, await stderr.WaitAsync(ChildDeadline));
            _ = await stdout.WaitAsync(ChildDeadline);
            return child.ExitCode;
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: false); await child.WaitForExitAsync().WaitAsync(ChildDeadline); }
        }
    }

    internal static async Task<int> RunChildAsync(string sessionId, string mode)
    {
        if (mode is not ("fault-1" or "fault-2" or "fault-3" or "resume")) return 64;
        try
        {
            using var session = ProcessInventorySession.OpenChild(sessionId);
            using var lease = ProfileLease.Acquire(session.Paths); session.ValidateProfileBoundary();
            var plan = session.ReadReconciliationPlan();
            var store = new SqliteInventoryStore(session.Paths, point =>
            { if (mode == "fault-" + (int)point) Environment.Exit(74); });
            using var deadline = new CancellationTokenSource(ChildDeadline);
            var result = await new InventoryReconciler(new SuppliedSource(session.Snapshot), store).RunAsync(plan, 1, deadline.Token);
            Console.Out.WriteLine(JsonSerializer.Serialize(result)); Console.Out.Flush(); return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error is InventoryIndexException index ? index.Code : error.GetType().Name);
            Console.Error.Flush(); return 75;
        }
    }
}
