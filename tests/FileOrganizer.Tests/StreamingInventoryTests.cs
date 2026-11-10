using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;
using static FileOrganizer.Tests.InventoryProcessTests;

namespace FileOrganizer.Tests;

public sealed class StreamingInventoryTests
{
    private const string TaskName = "FO-005-streaming-20261029";
    private static InventoryCaptureOptions Options => new()
    {
        PageSize = 7, MaxEntries = 5000, MaxDirectories = 100, MaxDuration = TimeSpan.FromSeconds(60),
    };

    [Fact]
    public async Task StreamingGenerationRejectsImpossibleReserveWithoutOverflow()
    {
        var error = await Assert.ThrowsAsync<FixtureSafetyException>(() => StreamingFixture.CreateAsync(new()
        { FileCount = 1, DirectoryCount = 1, TotalBytes = 32, MinimumFreeBytes = long.MaxValue }));
        Assert.Equal("streaming.generation_free_space", error.Code);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task StreamingDeniedDirectoryAndJunctionRemainExplicitCoverageBoundaries()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 30, DirectoryCount = 3, TotalBytes = 960 });
        using var sentinel = await StreamingFixture.CreateAsync(new() { FileCount = 2, DirectoryCount = 1, TotalBytes = 64 });
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        var source = new FixtureStreamingSource(fixture);
        var store = new SqliteInventoryStore(session.Paths);
        var old = await store.CaptureAsync(source, Options, null, CancellationToken.None);
        InventoryBoundaryTests.CreateJunction(Path.Combine(fixture.Root, "outside"), sentinel.Root);
        var directory = new DirectoryInfo(Path.Combine(fixture.Root, "d000"));
        var descriptor = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group).GetSecurityDescriptorBinaryForm();
        using var identity = WindowsIdentity.GetCurrent();
        var restricted = directory.GetAccessControl(AccessControlSections.Access);
        restricted.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        InventoryIndexResult incomplete;
        try
        {
            directory.SetAccessControl(restricted);
            incomplete = await store.CaptureAsync(source, Options, null, CancellationToken.None);
            Assert.False(incomplete.Published);
            Assert.Equal(InventoryRunStatus.Incomplete, incomplete.Status);
            Assert.Equal(old.GenerationId, Assert.Single((await store.ReadStateAsync(CancellationToken.None)).Roots).GenerationId);
            Assert.Equal(0L, Scalar(session.Paths, "SELECT count(*) FROM InventoryDirectoryEntry WHERE RelativePath LIKE 'outside/%';"));
            Assert.Equal(2L, Scalar(session.Paths, "SELECT Kind FROM InventoryDirectoryEntry WHERE RelativePath='outside';"));
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            directory.SetAccessControl(restore);
            Assert.Equal(descriptor, directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group).GetSecurityDescriptorBinaryForm());
        }
        await fixture.VerifyUnchangedAsync(CancellationToken.None);
        await sentinel.VerifyUnchangedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task EntryQuotaStopsWithoutPublishingAndPreservesDurableEvidence()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 30, DirectoryCount = 3, TotalBytes = 960 });
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        var store = new SqliteInventoryStore(session.Paths);
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() => store.CaptureAsync(new FixtureStreamingSource(fixture),
            Options with { MaxEntries = 10 }, null, CancellationToken.None));
        Assert.Contains("limit", error.Code);
        Assert.Empty((await store.ReadStateAsync(CancellationToken.None)).Roots);
        Assert.True(Convert.ToInt64(Scalar(session.Paths, "SELECT count(*) FROM InventoryScan;")) > 0);
        await fixture.VerifyUnchangedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task FailedSchemaThreeMigrationRollsBackWithoutReplacingPublishedSchemaTwoGeneration()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 6, DirectoryCount = 2, TotalBytes = 192 });
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        var source = new FixtureStreamingSource(fixture);
        var store = new SqliteInventoryStore(session.Paths);
        var old = await new InventoryIndexer(source, store).RunAsync();
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = session.Paths.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE CaptureMigrationProbe(Value INTEGER); CREATE TRIGGER FailCaptureMigration BEFORE INSERT ON SchemaMigration WHEN NEW.Version=3 BEGIN INSERT INTO CaptureMigrationProbe VALUES(1); SELECT RAISE(FAIL,'fixture migration fault'); END;";
            command.ExecuteNonQuery();
        }
        await Assert.ThrowsAsync<InventoryIndexException>(() => store.CaptureAsync(source, Options, null, CancellationToken.None));
        Assert.Equal(2L, Scalar(session.Paths, "PRAGMA user_version;"));
        Assert.Equal(0L, Scalar(session.Paths, "SELECT count(*) FROM CaptureMigrationProbe;"));
        Assert.Equal(0L, Scalar(session.Paths, "SELECT count(*) FROM sqlite_master WHERE name='InventoryCapture';"));
        Assert.Equal(old.GenerationId, Assert.Single((await store.ReadStateAsync(CancellationToken.None)).Roots).GenerationId);
        Assert.Equal("ok", Scalar(session.Paths, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task NativeStreamingExceedsLegacySnapshotCapAndPreservesGeneratedFiles()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 1000, DirectoryCount = 10, TotalBytes = 32000 });
        var source = new FixtureStreamingSource(fixture);
        await Assert.ThrowsAnyAsync<Exception>(() => source.CaptureAsync(CancellationToken.None));
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        var result = await new SqliteInventoryStore(session.Paths).CaptureAsync(source, Options, null, CancellationToken.None);
        Assert.True(result.Published);
        Assert.False(result.IsAtomicSnapshot);
        Assert.Equal(1010, result.CommittedEntries);
        Assert.Equal(3L, Scalar(session.Paths, "PRAGMA user_version;"));
        var root = Assert.Single((await new SqliteInventoryStore(session.Paths).ReadStateAsync(CancellationToken.None)).Roots);
        Assert.Equal(1010, root.EntryCount);
        var unchanged = await fixture.VerifyUnchangedAsync(CancellationToken.None);
        Assert.Equal(1000, unchanged.FileCount);
        Assert.Equal(32000, unchanged.TotalBytes);
    }

    [Fact]
    public async Task CaptureCancellationPersistsFrontierAndResumesSameGeneration()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 400, DirectoryCount = 10, TotalBytes = 12800 });
        var source = new FixtureStreamingSource(fixture);
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        var store = new SqliteInventoryStore(session.Paths);
        var prior = await store.CaptureAsync(source, Options, null, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        string? generation = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CaptureAsync(source, Options, null,
            cancellation.Token, new ProgressAction(value =>
            {
                if (value.Phase == InventoryCapturePhase.Capturing && value.CapturedEntries >= 7)
                { generation = value.GenerationId; cancellation.Cancel(); }
            })));
        var state = await store.ReadStateAsync(CancellationToken.None);
        Assert.Equal(prior.GenerationId, Assert.Single(state.Roots).GenerationId);
        var cancelled = Assert.Single(state.Runs, run => run.GenerationId == generation);
        Assert.Equal(InventoryRunStatus.Cancelled, cancelled.Status);
        Assert.True(cancelled.CommittedEntries >= 7);
        var resumed = await new SqliteInventoryStore(session.Paths).CaptureAsync(source, Options, generation, CancellationToken.None);
        Assert.Equal(generation, resumed.GenerationId);
        Assert.True(resumed.Published);
        Assert.Equal(410, resumed.CommittedEntries);
        await fixture.VerifyUnchangedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ResumeCannotResetAnExpiredPersistedCaptureDeadline()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 30, DirectoryCount = 3, TotalBytes = 960 });
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        var source = new FixtureStreamingSource(fixture);
        var store = new SqliteInventoryStore(session.Paths);
        var previous = await store.CaptureAsync(source, Options, null, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        string? generation = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CaptureAsync(source, Options, null,
            cancellation.Token, new ProgressAction(value =>
            {
                if (value.Phase == InventoryCapturePhase.Capturing && value.CapturedEntries >= 7)
                { generation = value.GenerationId; cancellation.Cancel(); }
            })));
        Assert.NotNull(generation);
        var before = Assert.Single((await store.ReadStateAsync(CancellationToken.None)).Runs, run => run.GenerationId == generation);
        var expiredDeadline = DateTimeOffset.UnixEpoch.ToString("O");
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = session.Paths.DatabasePath, Pooling = false }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            // Fault injection changes only the owned test database, never source metadata.
            command.CommandText = "UPDATE InventoryCapture SET DeadlineUtc=$deadline WHERE GenerationId=$generation;";
            command.Parameters.AddWithValue("$deadline", expiredDeadline);
            command.Parameters.AddWithValue("$generation", generation);
            Assert.Equal(1, command.ExecuteNonQuery());
        }
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() =>
            new SqliteInventoryStore(session.Paths).CaptureAsync(source, Options, generation, CancellationToken.None));
        Assert.Equal("inventory.capture_time_limit", error.Code);
        var after = await store.ReadStateAsync(CancellationToken.None);
        Assert.Equal(previous.GenerationId, Assert.Single(after.Roots).GenerationId);
        Assert.Equal(before, Assert.Single(after.Runs, run => run.GenerationId == generation));
        Assert.Equal(expiredDeadline, Scalar(session.Paths, $"SELECT DeadlineUtc FROM InventoryCapture WHERE GenerationId='{generation}';"));
        await fixture.VerifyUnchangedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AbruptTerminationDuringNativeCaptureReplaysUnfinishedDirectoryInFreshProcess()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 1000, DirectoryCount = 10, TotalBytes = 32000 });
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        var source = new FixtureStreamingSource(fixture);
        InventoryIndexResult previous;
        using (var lease = ProfileLease.Acquire(session.Paths))
            previous = await new SqliteInventoryStore(session.Paths).CaptureAsync(source, Options, null, CancellationToken.None);
        InventoryCaptureProgress paused;
        int killedPid;
        using (var child = StartChild(session, "pause", fixture.Id))
        {
            var stderr = ReadBoundedText(child.StandardError);
            try
            {
                paused = JsonSerializer.Deserialize<InventoryCaptureProgress>(await ReadBoundedLine(child.StandardOutput).WaitAsync(TimeSpan.FromSeconds(75)))!;
                Assert.Equal(InventoryCapturePhase.Capturing, paused.Phase);
                Assert.True(paused.CapturedEntries >= 7);
                Assert.False(child.HasExited);
                killedPid = child.Id;
                child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.NotEqual(0, child.ExitCode);
                Assert.Equal(string.Empty, await stderr.WaitAsync(TimeSpan.FromSeconds(15)));
            }
            finally { await KillOwnedChildIfAlive(child); }
        }
        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            var state = await new SqliteInventoryStore(session.Paths).ReadStateAsync(CancellationToken.None);
            var run = Assert.Single(state.Runs, item => item.GenerationId == paused.GenerationId);
            Assert.Equal(InventoryRunStatus.Running, run.Status);
            Assert.Equal(paused.CapturedEntries, run.CommittedEntries);
            Assert.Equal(previous.GenerationId, Assert.Single(state.Roots).GenerationId);
            session.WriteReceipt("capture-killed.json", JsonSerializer.SerializeToUtf8Bytes(new { killedPid, paused, previous.GenerationId }));
        }
        using (var child = StartChild(session, "resume", fixture.Id, paused.GenerationId))
        {
            var stdout = ReadBoundedText(child.StandardOutput);
            var stderr = ReadBoundedText(child.StandardError);
            try
            {
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(75));
                var diagnostic = await stderr.WaitAsync(TimeSpan.FromSeconds(15));
                Assert.True(child.ExitCode == 0, $"Capture child exit={child.ExitCode}; {diagnostic}");
                Assert.Equal(string.Empty, diagnostic);
                var result = JsonSerializer.Deserialize<InventoryIndexResult>((await stdout.WaitAsync(TimeSpan.FromSeconds(15))).Trim())!;
                Assert.Equal(paused.GenerationId, result.GenerationId);
                Assert.True(result.Published);
                Assert.Equal(1010, result.CommittedEntries);
            }
            finally { await KillOwnedChildIfAlive(child); }
        }
        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            var state = await new SqliteInventoryStore(session.Paths).ReadStateAsync(CancellationToken.None);
            Assert.Equal(paused.GenerationId, Assert.Single(state.Roots).GenerationId);
            Assert.Equal(1010, Assert.Single(state.Roots).EntryCount);
            Assert.Equal("ok", Scalar(session.Paths, "PRAGMA integrity_check;"));
        }
        var verification = await fixture.VerifyUnchangedAsync(CancellationToken.None);
        session.WriteReceipt("capture-verified.json", JsonSerializer.SerializeToUtf8Bytes(new { paused.GenerationId, verification }));
    }

    [Fact]
    public async Task ChangedTreeBetweenCaptureAndVerificationCannotPublishOverPriorGeneration()
    {
        using var fixture = await StreamingFixture.CreateAsync(new() { FileCount = 50, DirectoryCount = 10, TotalBytes = 1600 });
        using var session = ProcessInventorySession.Create(new(fixture.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        var source = new FixtureStreamingSource(fixture);
        var store = new SqliteInventoryStore(session.Paths);
        var mutable = Path.Combine(fixture.Root, "zz-mutable.bin");
        File.WriteAllBytes(mutable, [1]);
        var old = await store.CaptureAsync(source, Options, null, CancellationToken.None);
        var changed = false;
        var result = await store.CaptureAsync(source, Options, null, CancellationToken.None, new ProgressAction(value =>
        {
            if (!changed && value.Phase == InventoryCapturePhase.Capturing && value.CapturedEntries >= 11)
            {
                File.WriteAllBytes(mutable, [1, 2, 3]);
                changed = true; // Only the explicit fault actor mutates an unplanned disposable leaf.
            }
        }));
        Assert.True(changed);
        Assert.False(result.Published);
        Assert.Equal(InventoryRunStatus.Incomplete, result.Status);
        Assert.Equal(old.GenerationId, Assert.Single((await store.ReadStateAsync(CancellationToken.None)).Roots).GenerationId);
        await fixture.VerifyUnchangedAsync(CancellationToken.None);
    }

    [Fact]
    public async Task WrongSelectedRootCannotResumeAnotherRootsDurableFrontier()
    {
        using var first = await StreamingFixture.CreateAsync(new() { FileCount = 30, DirectoryCount = 10, TotalBytes = 960 });
        using var second = await StreamingFixture.CreateAsync(new() { FileCount = 30, DirectoryCount = 10, TotalBytes = 960 });
        using var session = ProcessInventorySession.Create(new(first.RootIdentity, []), TaskName);
        using var lease = ProfileLease.Acquire(session.Paths);
        using var cancellation = new CancellationTokenSource();
        string? generation = null;
        var store = new SqliteInventoryStore(session.Paths);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CaptureAsync(new FixtureStreamingSource(first), Options, null,
            cancellation.Token, new ProgressAction(value => { generation = value.GenerationId; cancellation.Cancel(); })));
        await Assert.ThrowsAsync<InventoryIndexException>(() => store.CaptureAsync(new FixtureStreamingSource(second), Options, generation, CancellationToken.None));
        Assert.Empty((await store.ReadStateAsync(CancellationToken.None)).Roots);
    }

    private sealed class ProgressAction(Action<InventoryCaptureProgress> action) : IProgress<InventoryCaptureProgress>
    {
        public void Report(InventoryCaptureProgress value) => action(value);
    }
}
