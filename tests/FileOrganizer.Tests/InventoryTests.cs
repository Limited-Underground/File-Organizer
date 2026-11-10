using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class InventoryTests
{
    private static readonly DateTime ObservedUtc = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task NativeFixtureEnumerationPersistsActualEntriesAndSurvivesStoreRestart()
    {
        using var workspace = FixtureWorkspace.Create(evidenceTask: "FO-005-indexing-20261029");
        await new FixtureGenerator().GenerateAsync(workspace, new(104729,
            [new("one.bin", 17, "one"), new("nested/two.bin", 31, "two")], ["empty"]));
        // This entry was never in the generator manifest: the source must enumerate the real fixture.
        File.WriteAllBytes(Path.Combine(workspace.Root, "unplanned.bin"), [7, 8, 9]);
        var source = new FixtureInventorySource(workspace);
        var captured = await source.CaptureAsync(CancellationToken.None);
        Assert.Contains(captured.Entries, entry => entry.RelativePath == "unplanned.bin" && entry.Length == 3);
        Assert.Equal(5, captured.Entries.Count);
        Assert.DoesNotContain(captured.Entries, entry => entry.RelativePath is "manifest.json" or "receipt.json" or ".file-organizer-fixture-owner");

        var paths = await NewStorePaths();
        var result = await new InventoryIndexer(source, new SqliteInventoryStore(paths)).RunAsync(batchSize: 2);
        Assert.Equal(InventoryRunStatus.Completed, result.Status);
        Assert.True(result.Published);
        Assert.Equal(captured.Entries.Count, result.CommittedEntries);
        var root = Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Roots);
        Assert.Equal(captured.RootIdentity, root.RootIdentity);
        Assert.Equal(result.GenerationId, root.GenerationId);
        Assert.Equal(5, root.EntryCount);
        Assert.Equal(5, root.ObjectCount);
        Assert.Equal(2, (await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None)).SchemaVersion);
        Assert.Equal("ok", Scalar(paths, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void FingerprintIsCanonicalAcrossEntryOrderAndChangesWithObservedMetadata()
    {
        var original = Snapshot(FileEntry("b.bin", "object-b", 5), FileEntry("a.bin", "object-a", 3));
        var reversed = original with { Entries = original.Entries.Reverse().ToArray() };
        Assert.Equal(PreparedInventorySnapshot.Create(original).Fingerprint, PreparedInventorySnapshot.Create(reversed).Fingerprint);
        var changed = Snapshot(FileEntry("b.bin", "object-b", 6), FileEntry("a.bin", "object-a", 3));
        Assert.NotEqual(PreparedInventorySnapshot.Create(original).Fingerprint, PreparedInventorySnapshot.Create(changed).Fingerprint);
    }

    [Fact]
    public async Task CancellationAfterDurableBatchResumesSameGenerationAcrossNewStoreInstances()
    {
        var paths = await NewStorePaths();
        var snapshot = Snapshot(FileEntry("a", "a"), FileEntry("b", "b"), FileEntry("c", "c"), FileEntry("d", "d"));
        using var cancellation = new CancellationTokenSource();
        var progress = new InlineProgress(result =>
        {
            if (result.CommittedEntries == 2 && result.Status == InventoryRunStatus.Running) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteInventoryStore(paths)
            .IndexAsync(snapshot, 2, cancellation.Token, progress));
        var interrupted = Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Runs);
        Assert.Equal(2, interrupted.CommittedEntries);
        Assert.Empty((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Roots);

        var resumed = await new SqliteInventoryStore(paths).IndexAsync(snapshot, 1, CancellationToken.None);
        Assert.Equal(interrupted.GenerationId, resumed.GenerationId);
        Assert.Equal(4, resumed.CommittedEntries);
        Assert.True(resumed.Published);
        var state = await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None);
        Assert.Single(state.Runs);
        Assert.Equal(4, Assert.Single(state.Roots).EntryCount);
    }

    [Fact]
    public async Task ChangedSnapshotCannotResumeOldGenerationOrPublishItsPartialRows()
    {
        var paths = await NewStorePaths();
        var original = Snapshot(FileEntry("a", "a"), FileEntry("b", "b"), FileEntry("c", "c"));
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteInventoryStore(paths).IndexAsync(original, 1,
            cancellation.Token, new InlineProgress(result =>
            {
                if (result.CommittedEntries == 1 && result.Status == InventoryRunStatus.Running) cancellation.Cancel();
            })));
        var old = Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Runs);
        var changed = Snapshot(FileEntry("replacement", "replacement", 12));
        var completed = await new SqliteInventoryStore(paths).IndexAsync(changed, 1, CancellationToken.None);
        Assert.NotEqual(old.GenerationId, completed.GenerationId);
        var state = await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None);
        Assert.Equal(2, state.Runs.Count);
        Assert.Equal(completed.GenerationId, Assert.Single(state.Roots).GenerationId);
        Assert.Equal(1, Assert.Single(state.Roots).EntryCount);
    }

    [Fact]
    public async Task IncompleteScanRetainsPreviousPublishedRootAndRecordsCoverageFailure()
    {
        var paths = await NewStorePaths();
        var store = new SqliteInventoryStore(paths);
        var accepted = await store.IndexAsync(Snapshot(FileEntry("safe", "safe")), 1, CancellationToken.None);
        var errorEntry = new InventoryEntry("unreadable", InventoryEntryKind.File, string.Empty, 0, ObservedUtc, 0, "filesystem.access_denied");
        var incomplete = await store.IndexAsync(Snapshot(FileEntry("different", "different"), errorEntry), 1, CancellationToken.None);
        Assert.Equal(InventoryRunStatus.Incomplete, incomplete.Status);
        Assert.False(incomplete.Published);
        var state = await store.ReadStateAsync(CancellationToken.None);
        Assert.Equal(accepted.GenerationId, Assert.Single(state.Roots).GenerationId);
        Assert.Equal(1, Assert.Single(state.Roots).EntryCount);
        Assert.Contains(state.Runs, run => run.GenerationId == incomplete.GenerationId && run.Status == InventoryRunStatus.Incomplete);
    }

    [Fact]
    public async Task HardLinkAliasesCountAsTwoEntriesAndOnePhysicalObject()
    {
        var paths = await NewStorePaths();
        var result = await new SqliteInventoryStore(paths).IndexAsync(
            Snapshot(FileEntry("first-link", "same-native-object", 27), FileEntry("second-link", "same-native-object", 27)),
            1, CancellationToken.None);
        Assert.True(result.Published);
        var root = Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Roots);
        Assert.Equal(2, root.EntryCount);
        Assert.Equal(1, root.ObjectCount);
    }

    [Fact]
    public async Task ExcludedReparseEntryIsExplicitWithoutInventingObjectOrIncompleteScan()
    {
        var paths = await NewStorePaths();
        var result = await new SqliteInventoryStore(paths).IndexAsync(
            Snapshot(new InventoryEntry("link", InventoryEntryKind.ExcludedReparse, string.Empty, 0, ObservedUtc, (long)FileAttributes.ReparsePoint)),
            1, CancellationToken.None);
        Assert.True(result.Published);
        var root = Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Roots);
        Assert.Equal(1, root.EntryCount);
        Assert.Equal(0, root.ObjectCount);
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("C:/escape")]
    [InlineData("/escape")]
    [InlineData("one\\two")]
    [InlineData("one//two")]
    [InlineData("one:stream")]
    [InlineData("NUL.txt")]
    [InlineData("trailing.")]
    public void InvalidObservedPathsAreRejectedBeforePersistence(string path)
    {
        Assert.Throws<InventoryIndexException>(() => PreparedInventorySnapshot.Create(Snapshot(FileEntry(path, "identity"))));
    }

    [Fact]
    public void CaseCollisionMissingParentDeepPathAndConflictingObjectMetadataAreRejected()
    {
        Assert.Throws<InventoryIndexException>(() => PreparedInventorySnapshot.Create(Snapshot(FileEntry("Same", "one"), FileEntry("same", "two"))));
        Assert.Throws<InventoryIndexException>(() => PreparedInventorySnapshot.Create(Snapshot(FileEntry("missing/file", "one"))));
        Assert.Throws<InventoryIndexException>(() => PreparedInventorySnapshot.Create(Snapshot(FileEntry(string.Join('/', Enumerable.Repeat("d", 17)), "one"))));
        Assert.Throws<InventoryIndexException>(() => PreparedInventorySnapshot.Create(Snapshot(FileEntry("a", "same", 1), FileEntry("b", "same", 2))));
        Assert.Throws<InventoryIndexException>(() => PreparedInventorySnapshot.Create(new("root", Enumerable.Range(0, 257).Select(index => FileEntry($"file-{index}", $"id-{index}")).ToArray())));
    }

    [Fact]
    public async Task CancellationBeforeIndexDoesNotAdvanceMigrationOrCreateScan()
    {
        var paths = await NewStorePaths();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteInventoryStore(paths)
            .IndexAsync(Snapshot(FileEntry("a", "a")), 1, cancellation.Token));
        Assert.Equal(1L, Scalar(paths, "PRAGMA user_version;"));
    }

    [Fact]
    public async Task CallbackFailureAfterDurableBatchRecordsInterruptedAndResumesWithoutDuplicateRows()
    {
        var paths = await NewStorePaths();
        var snapshot = Snapshot(FileEntry("a", "a"), FileEntry("b", "b"), FileEntry("c", "c"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new SqliteInventoryStore(paths).IndexAsync(snapshot, 2,
            CancellationToken.None, new InlineProgress(_ => throw new InvalidOperationException("fixture.interruption"))));
        var interrupted = Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Runs);
        Assert.Equal(InventoryRunStatus.Interrupted, interrupted.Status);
        Assert.Equal(2, interrupted.CommittedEntries);
        Assert.Equal(2L, Scalar(paths, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        var resumed = await new SqliteInventoryStore(paths).IndexAsync(snapshot, 2, CancellationToken.None);
        Assert.Equal(interrupted.GenerationId, resumed.GenerationId);
        Assert.True(resumed.Published);
        Assert.Equal(3L, Scalar(paths, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        Assert.Equal(3L, Scalar(paths, "SELECT count(*) FROM InventoryFileObject;"));
    }

    [Fact]
    public async Task FailedBatchRollsBackObjectsEntriesProbeAndCursorBeforeRetry()
    {
        var paths = await NewStorePaths();
        var store = new SqliteInventoryStore(paths);
        var prior = await store.IndexAsync(Snapshot(), 2, CancellationToken.None);
        Execute(paths, """
            CREATE TABLE InventoryFailureProbe(Value INTEGER);
            CREATE TRIGGER FailInventoryEntry BEFORE INSERT ON InventoryDirectoryEntry
            WHEN NEW.Name='b' BEGIN
                INSERT INTO InventoryFailureProbe VALUES(1);
                SELECT RAISE(FAIL,'injected fixture batch failure');
            END;
            """);
        var snapshot = Snapshot(FileEntry("a", "a"), FileEntry("b", "b"), FileEntry("c", "c"));
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() => store.IndexAsync(snapshot, 2, CancellationToken.None));
        Assert.Equal("inventory.unavailable", error.Code);
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM InventoryFailureProbe;"));
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM InventoryFileObject;"));
        var state = await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None);
        Assert.Equal(prior.GenerationId, Assert.Single(state.Roots).GenerationId);
        var interrupted = Assert.Single(state.Runs, run => run.Status == InventoryRunStatus.Interrupted);
        Assert.Equal(0, interrupted.CommittedEntries);
        Execute(paths, "DROP TRIGGER FailInventoryEntry;");
        var resumed = await new SqliteInventoryStore(paths).IndexAsync(snapshot, 2, CancellationToken.None);
        Assert.Equal(interrupted.GenerationId, resumed.GenerationId);
        Assert.True(resumed.Published);
        Assert.Equal(3L, Scalar(paths, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        Assert.Equal("ok", Scalar(paths, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task InventoryMigrationBacksUpVerifiedVersionOneBeforeAdvancingToVersionTwo()
    {
        var paths = await NewStorePaths();
        var profile = Scalar(paths, "SELECT ProfileId FROM AppState;");
        var startupCount = Scalar(paths, "SELECT StartupCount FROM AppState;");
        var firstChecksum = Scalar(paths, "SELECT Checksum FROM SchemaMigration WHERE Version=1;");
        await new SqliteInventoryStore(paths).IndexAsync(Snapshot(FileEntry("a", "a")), 1, CancellationToken.None);
        Assert.Equal(2L, Scalar(paths, "PRAGMA user_version;"));
        Assert.Equal(2L, Scalar(paths, "SELECT count(*) FROM SchemaMigration;"));
        Assert.Equal(profile, Scalar(paths, "SELECT ProfileId FROM AppState;"));
        Assert.Equal(startupCount, Scalar(paths, "SELECT StartupCount FROM AppState;"));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(paths.DataDirectory, "backups"), "before-inventory-v2-*.db"));
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backup, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        connection.Open();
        Assert.Equal(1L, BackupScalar("PRAGMA user_version;"));
        Assert.Equal("ok", BackupScalar("PRAGMA integrity_check;"));
        Assert.Equal(1L, BackupScalar("SELECT count(*) FROM SchemaMigration;"));
        Assert.Equal(firstChecksum, BackupScalar("SELECT Checksum FROM SchemaMigration WHERE Version=1;"));
        Assert.Equal(profile, BackupScalar("SELECT ProfileId FROM AppState;"));
        Assert.Equal(startupCount, BackupScalar("SELECT StartupCount FROM AppState;"));
        Assert.Equal(0L, BackupScalar("SELECT count(*) FROM sqlite_master WHERE name='InventoryScan';"));

        object? BackupScalar(string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
    }

    [Fact]
    public async Task InvalidMigrationChainRefusesInventoryMutationWithoutReplacingEvidence()
    {
        var paths = await NewStorePaths();
        Execute(paths, "UPDATE SchemaMigration SET Checksum='fixture-invalid-chain';");
        var error = await Assert.ThrowsAsync<StoreInitializationException>(() => new SqliteInventoryStore(paths)
            .IndexAsync(Snapshot(FileEntry("a", "a")), 1, CancellationToken.None));
        Assert.Equal("store.migration_mismatch", error.ErrorCode);
        Assert.Equal(1L, Scalar(paths, "PRAGMA user_version;"));
        Assert.Equal("fixture-invalid-chain", Scalar(paths, "SELECT Checksum FROM SchemaMigration;"));
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'Inventory%';"));
        Assert.False(Directory.Exists(Path.Combine(paths.DataDirectory, "backups")));
    }

    [Fact]
    public async Task FailedVersionTwoMigrationRollsBackSchemaAndProbePreservingVerifiedBackupForRetry()
    {
        var paths = await NewStorePaths();
        var profile = Scalar(paths, "SELECT ProfileId FROM AppState;");
        var startupCount = Scalar(paths, "SELECT StartupCount FROM AppState;");
        var checksum = Scalar(paths, "SELECT Checksum FROM SchemaMigration WHERE Version=1;");
        Execute(paths, """
            CREATE TABLE MigrationFailureProbe(Value INTEGER);
            CREATE TRIGGER FailVersionTwoMigration BEFORE INSERT ON SchemaMigration
            WHEN NEW.Version=2 BEGIN
                INSERT INTO MigrationFailureProbe VALUES(1);
                SELECT RAISE(FAIL,'injected fixture migration failure');
            END;
            """);
        var snapshot = Snapshot(FileEntry("a", "a"));
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() => new SqliteInventoryStore(paths)
            .IndexAsync(snapshot, 1, CancellationToken.None));
        Assert.Equal("inventory.unavailable", error.Code);
        Assert.Equal(1L, Scalar(paths, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar(paths, "SELECT count(*) FROM SchemaMigration;"));
        Assert.Equal(checksum, Scalar(paths, "SELECT Checksum FROM SchemaMigration WHERE Version=1;"));
        Assert.Equal(profile, Scalar(paths, "SELECT ProfileId FROM AppState;"));
        Assert.Equal(startupCount, Scalar(paths, "SELECT StartupCount FROM AppState;"));
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM sqlite_master WHERE name LIKE 'Inventory%';"));
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM MigrationFailureProbe;"));

        var backupPath = Assert.Single(Directory.GetFiles(Path.Combine(paths.DataDirectory, "backups"), "before-inventory-v2-*.db"));
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString()))
        {
            backup.Open();
            Assert.Equal("ok", Read("PRAGMA integrity_check;"));
            Assert.Equal(1L, Read("PRAGMA user_version;"));
            Assert.Equal(1L, Read("SELECT count(*) FROM SchemaMigration;"));
            Assert.Equal(checksum, Read("SELECT Checksum FROM SchemaMigration WHERE Version=1;"));
            Assert.Equal(profile, Read("SELECT ProfileId FROM AppState;"));
            Assert.Equal(startupCount, Read("SELECT StartupCount FROM AppState;"));
            Assert.Equal(0L, Read("SELECT count(*) FROM MigrationFailureProbe;"));
            Assert.Equal(0L, Read("SELECT count(*) FROM sqlite_master WHERE name LIKE 'Inventory%';"));

            object? Read(string sql)
            {
                using var command = backup.CreateCommand();
                command.CommandText = sql;
                return command.ExecuteScalar();
            }
        }

        Execute(paths, "DROP TRIGGER FailVersionTwoMigration;");
        var retry = await new SqliteInventoryStore(paths).IndexAsync(snapshot, 1, CancellationToken.None);
        Assert.True(retry.Published);
        Assert.Equal(InventoryRunStatus.Completed, retry.Status);
        Assert.Equal(2L, Scalar(paths, "PRAGMA user_version;"));
        Assert.Equal(2L, Scalar(paths, "SELECT count(*) FROM SchemaMigration;"));
        Assert.Equal(profile, Scalar(paths, "SELECT ProfileId FROM AppState;"));
        Assert.Equal(startupCount, Scalar(paths, "SELECT StartupCount FROM AppState;"));
        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM MigrationFailureProbe;"));
        Assert.Equal("ok", Scalar(paths, "PRAGMA integrity_check;"));
        Assert.True(File.Exists(backupPath));
    }

    [Fact]
    public async Task CompletedFingerprintIsIdempotentAndReobservedStateCanBecomeCurrentAgain()
    {
        var paths = await NewStorePaths();
        var first = Snapshot(FileEntry("first", "first"));
        var old = await new SqliteInventoryStore(paths).IndexAsync(first, 1, CancellationToken.None);
        var repeated = await new SqliteInventoryStore(paths).IndexAsync(first, 1, CancellationToken.None);
        Assert.Equal(old.GenerationId, repeated.GenerationId);
        Assert.Equal(1L, Scalar(paths, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        var latest = await new SqliteInventoryStore(paths).IndexAsync(Snapshot(FileEntry("second", "second")), 1, CancellationToken.None);
        var oldAgain = await new SqliteInventoryStore(paths).IndexAsync(first, 1, CancellationToken.None);
        Assert.NotEqual(old.GenerationId, latest.GenerationId);
        Assert.True(oldAgain.Published);
        Assert.Equal(old.GenerationId, oldAgain.GenerationId);
        Assert.Equal(old.GenerationId, Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Roots).GenerationId);
        Assert.Equal(2L, Scalar(paths, "SELECT count(*) FROM InventoryScan;"));
    }

    [Theory]
    [InlineData("UPDATE InventoryDirectoryEntry SET Length=Length+1 WHERE RelativePath='a';")]
    [InlineData("UPDATE InventoryFileObject SET Length=Length+1 WHERE ObjectIdentity='a';")]
    [InlineData("UPDATE InventoryDirectoryEntry SET RelativePath='changed-name',Name='changed-name' WHERE RelativePath='a';")]
    public async Task SavedPrefixMetadataTamperCannotResumeEvenWhenRowCountsMatch(string tamper)
    {
        var paths = await NewStorePaths();
        var snapshot = Snapshot(FileEntry("a", "a"), FileEntry("b", "b"), FileEntry("c", "c"));
        await InterruptAfterTwoEntries(paths, snapshot);
        Execute(paths, tamper);
        Assert.Equal(2L, Scalar(paths, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() => new SqliteInventoryStore(paths)
            .IndexAsync(snapshot, 1, CancellationToken.None));
        Assert.Equal("inventory.invalid_checkpoint", error.Code);
        Assert.Equal(2L, Scalar(paths, "SELECT CommittedEntries FROM InventoryScan;"));
        Assert.Equal(2L, Scalar(paths, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        Assert.Empty((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Roots);
    }

    [Theory]
    [InlineData("UPDATE InventoryScan SET CommittedEntries=1;")]
    [InlineData("UPDATE InventoryDirectoryEntry SET Ordinal=7 WHERE RelativePath='b';")]
    [InlineData("INSERT INTO InventoryDirectoryEntry SELECT GenerationId,2,'unexpected','','unexpected',Kind,ObjectIdentity,Length,ModifiedUtc,Attributes,ErrorCode FROM InventoryDirectoryEntry WHERE RelativePath='a';")]
    public async Task CursorOrExtraEntryTamperCannotBeMistakenForDurablePrefix(string tamper)
    {
        var paths = await NewStorePaths();
        var snapshot = Snapshot(FileEntry("a", "a"), FileEntry("b", "b"), FileEntry("c", "c"));
        await InterruptAfterTwoEntries(paths, snapshot);
        Execute(paths, tamper);
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() => new SqliteInventoryStore(paths)
            .IndexAsync(snapshot, 1, CancellationToken.None));
        Assert.Equal("inventory.invalid_checkpoint", error.Code);
        Assert.Empty((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Roots);
        Assert.Equal(InventoryRunStatus.Cancelled, Assert.Single((await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None)).Runs).Status);
    }

    private static async Task InterruptAfterTwoEntries(AppDataPaths paths, InventorySnapshot snapshot)
    {
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SqliteInventoryStore(paths).IndexAsync(snapshot, 2,
            cancellation.Token, new InlineProgress(result =>
            {
                if (result.CommittedEntries == 2 && result.Status == InventoryRunStatus.Running) cancellation.Cancel();
            })));
    }

    private static InventoryEntry FileEntry(string path, string identity, long length = 1) =>
        new(path, InventoryEntryKind.File, identity, length, ObservedUtc, (long)FileAttributes.Normal);

    private static InventorySnapshot Snapshot(params InventoryEntry[] entries) => new("fixture-root-identity", entries);

    private static async Task<AppDataPaths> NewStorePaths()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        return paths;
    }

    private static object? Scalar(AppDataPaths paths, string sql)
    {
        using var connection = Open(paths);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(AppDataPaths paths, string sql)
    {
        using var connection = Open(paths);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(AppDataPaths paths)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class InlineProgress(Action<InventoryIndexResult> callback) : IProgress<InventoryIndexResult>
    {
        public void Report(InventoryIndexResult value) => callback(value);
    }
}
