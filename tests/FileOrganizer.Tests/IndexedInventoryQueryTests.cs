using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventoryQueryTests
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task MissingDatabaseReadDoesNotCreateDatabaseAndPrecancelDoesNotCreateData()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        var reader = new SqliteIndexedInventoryReader(paths);
        Assert.Empty((await reader.ReadCatalogsAsync(new(), CancellationToken.None)).Catalogs);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadCatalogsAsync(new(), cancellation.Token));
        Assert.False(File.Exists(paths.DatabasePath));
        Assert.False(Directory.Exists(paths.DataDirectory));
    }

    [Fact]
    public async Task FreshSchemaOneReadIsEmptyWithoutMigrationOrPersistentContentChange()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        using var lease = ProfileLease.Acquire(paths);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var before = FingerprintFiles(paths.DataDirectory);
        Assert.Empty((await new SqliteIndexedInventoryReader(paths).ReadCatalogsAsync(new(), CancellationToken.None)).Catalogs);
        Assert.Equal(before, FingerprintFiles(paths.DataDirectory));
        Assert.Equal(1L, Scalar(paths, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar(paths, "SELECT StartupCount FROM AppState;"));
    }

    [Fact]
    public async Task PagesRemainPinnedToCompletedGenerationWhenNewPublicationArrives()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        using var lease = ProfileLease.Acquire(paths);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(paths);
        var first = await store.IndexAsync(Snapshot("root-a", "e", "a", "d", "b", "c"), 2, CancellationToken.None);
        var reader = new SqliteIndexedInventoryReader(paths);
        var firstPage = await reader.ReadPageAsync(new("root-a", first.GenerationId, 2), CancellationToken.None);
        Assert.Equal(new[] { "a", "b" }, firstPage.Rows.Select(row => row.RelativePath));
        Assert.True(firstPage.IsCurrentPublication);
        Assert.NotNull(firstPage.NextCursor);
        var latest = await store.IndexAsync(Snapshot("root-a", "new"), 1, CancellationToken.None);
        var secondPage = await reader.ReadPageAsync(new("root-a", first.GenerationId, 2, firstPage.NextCursor), CancellationToken.None);
        Assert.Equal(new[] { "c", "d" }, secondPage.Rows.Select(row => row.RelativePath));
        Assert.False(secondPage.IsCurrentPublication);
        Assert.Equal(latest.GenerationId, secondPage.CurrentPublishedGenerationId);
        var finalPage = await reader.ReadPageAsync(new("root-a", first.GenerationId, 2, secondPage.NextCursor), CancellationToken.None);
        Assert.Equal("e", Assert.Single(finalPage.Rows).RelativePath);
        Assert.Null(finalPage.NextCursor);
        var refreshed = Assert.Single((await reader.ReadCatalogsAsync(new(), CancellationToken.None)).Catalogs);
        Assert.Equal(latest.GenerationId, refreshed.GenerationId);
        Assert.Equal(1, refreshed.EntryCount);
        Assert.False(finalPage.Catalog.IsAtomicSnapshot);
        Assert.False(finalPage.Catalog.LiveAvailabilityKnown);
    }

    [Fact]
    public async Task CursorCannotCrossRootGenerationOrPageSizeAndReadsLeaveDatabaseUnchanged()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        using var lease = ProfileLease.Acquire(paths);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(paths);
        var a = await store.IndexAsync(Snapshot("root-a", "a", "b", "c"), 1, CancellationToken.None);
        var b = await store.IndexAsync(Snapshot("root-b", "a", "b", "c"), 1, CancellationToken.None);
        var reader = new SqliteIndexedInventoryReader(paths);
        var before = FingerprintFiles(paths.DataDirectory);
        var page = await reader.ReadPageAsync(new("root-a", a.GenerationId, 1), CancellationToken.None);
        Assert.NotNull(page.NextCursor);
        foreach (var request in new[]
        {
            new IndexedInventoryRequest("root-b", b.GenerationId, 1, page.NextCursor),
            new IndexedInventoryRequest("root-a", b.GenerationId, 1, page.NextCursor),
            new IndexedInventoryRequest("root-a", a.GenerationId, 2, page.NextCursor),
        })
        {
            var failure = await Assert.ThrowsAsync<InventoryIndexException>(() => reader.ReadPageAsync(request, CancellationToken.None));
            Assert.Equal("inventory.query_cursor_mismatch", failure.Code);
        }
        Assert.Equal(before, FingerprintFiles(paths.DataDirectory));
    }

    [Fact]
    public async Task RetainedOnlyRootIsVisibleWithoutExposingIncompleteStagingRows()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        using var lease = ProfileLease.Acquire(paths);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(paths);
        var snapshot = new InventorySnapshot("only-incomplete", [new("unreadable", InventoryEntryKind.File, "", 0, Epoch, 128, "filesystem.access_denied")]);
        var incomplete = await store.IndexAsync(snapshot, 1, CancellationToken.None);
        Assert.False(incomplete.Published);
        var reader = new SqliteIndexedInventoryReader(paths);
        var catalog = Assert.Single((await reader.ReadCatalogsAsync(new(), CancellationToken.None)).Catalogs);
        Assert.Equal("only-incomplete", catalog.RootIdentity);
        Assert.Null(catalog.GenerationId);
        Assert.False(catalog.HasPublishedGeneration);
        Assert.Equal(0, catalog.EntryCount);
        Assert.True(catalog.HasRetainedUnpublishedAttempts);
        var failure = await Assert.ThrowsAsync<InventoryIndexException>(() => reader.ReadPageAsync(
            new("only-incomplete", incomplete.GenerationId), CancellationToken.None));
        Assert.Equal("inventory.query_generation_incomplete", failure.Code);
    }

    [Fact]
    public async Task CatalogPagingIncludesRootsOnceAndFlagsRetainedAttemptsWithoutReplacingPublication()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        using var lease = ProfileLease.Acquire(paths);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(paths);
        var prior = await store.IndexAsync(Snapshot("b", "one"), 1, CancellationToken.None);
        await store.IndexAsync(new("b", [new("denied", InventoryEntryKind.File, "", 0, Epoch, 128, "filesystem.access_denied")]), 1, CancellationToken.None);
        await store.IndexAsync(Snapshot("a", "one"), 1, CancellationToken.None);
        await store.IndexAsync(Snapshot("c", "one"), 1, CancellationToken.None);
        var reader = new SqliteIndexedInventoryReader(paths);
        var first = await reader.ReadCatalogsAsync(new(2), CancellationToken.None);
        Assert.Equal(new[] { "a", "b" }, first.Catalogs.Select(catalog => catalog.RootIdentity));
        Assert.Equal(prior.GenerationId, first.Catalogs[1].GenerationId);
        Assert.True(first.Catalogs[1].HasRetainedUnpublishedAttempts);
        var last = await reader.ReadCatalogsAsync(new(2, first.NextAfterRootIdentity), CancellationToken.None);
        Assert.Equal("c", Assert.Single(last.Catalogs).RootIdentity);
        Assert.Null(last.NextAfterRootIdentity);
    }

    [IndexedUiLargeFact]
    public async Task ExistingMillionRowDisposableProfilePagesReadOnlyThroughRealReader()
    {
        var checkout = ProcessInventorySession.FindCheckout();
        var task = Path.Combine(checkout, "artifacts", "FO-012-indexed-ui-20261029");
        using var receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(task, "smoke-large-profile.json")));
        var profile = receipt.RootElement.GetProperty("profile").GetString()!;
        var id = Path.GetFileName(profile);
        Assert.True(Guid.TryParseExact(id, "N", out _));
        Assert.Equal(id.ToLowerInvariant(), id);
        var expectedProfile = Path.Combine(task, "smoke", id);
        Assert.Equal(Path.GetFullPath(expectedProfile), Path.GetFullPath(profile), ignoreCase: true);
        var paths = new AppDataPaths(expectedProfile);
        Assert.Equal(paths.DatabasePath, receipt.RootElement.GetProperty("database").GetString(), ignoreCase: true);
        Assert.True(receipt.RootElement.GetProperty("sourceUnchanged").GetBoolean());
        Assert.Equal(1_000_000, receipt.RootElement.GetProperty("counts").GetProperty("entries").GetInt32());
        using var ownership = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(expectedProfile, ".indexed-ui-profile-owner.json")));
        Assert.Equal(1, ownership.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal(id, ownership.RootElement.GetProperty("Id").GetString());
        Assert.Equal("FO-012-indexed-ui-20261029", ownership.RootElement.GetProperty("Task").GetString());
        for (var directory = new DirectoryInfo(paths.RootDirectory); directory is not null; directory = directory.Parent)
            Assert.False(directory.Exists && directory.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.True(File.Exists(paths.DatabasePath), "Prepare the named disposable UI profile before opting into this test.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        string before;
        using (var originalFile = File.OpenRead(paths.DatabasePath))
            before = Convert.ToHexString(await SHA256.HashDataAsync(originalFile, deadline.Token));
        var reader = new SqliteIndexedInventoryReader(paths);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var cancelTimer = Stopwatch.StartNew();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadCatalogsAsync(new(), cancelled.Token));
            Assert.True(cancelTimer.Elapsed < TimeSpan.FromSeconds(5));
        }
        var catalogs = new List<IndexedCatalog>();
        string? after = null;
        do
        {
            var page = await reader.ReadCatalogsAsync(new(50, after), deadline.Token);
            catalogs.AddRange(page.Catalogs);
            after = page.NextAfterRootIdentity;
        } while (after is not null);
        Assert.Equal(100, catalogs.Count);
        Assert.Equal(100, catalogs.Select(catalog => catalog.RootIdentity).Distinct().Count());
        var samples = new List<double>();
        foreach (var catalog in catalogs)
        {
            Assert.True(catalog.HasPublishedGeneration);
            Assert.Equal(10_000, catalog.EntryCount);
            var timer = Stopwatch.StartNew();
            var first = await reader.ReadPageAsync(new(catalog.RootIdentity, catalog.GenerationId!, 50), deadline.Token);
            samples.Add(timer.Elapsed.TotalMilliseconds);
            Assert.Equal(50, first.Rows.Count);
            Assert.True(first.IsCurrentPublication);
            Assert.NotNull(first.NextCursor);
            var second = await reader.ReadPageAsync(new(catalog.RootIdentity, catalog.GenerationId!, 50, first.NextCursor), deadline.Token);
            Assert.Empty(first.Rows.Select(row => row.RelativePath).Intersect(second.Rows.Select(row => row.RelativePath)));
            var repeated = await reader.ReadPageAsync(new(catalog.RootIdentity, catalog.GenerationId!, 50), deadline.Token);
            Assert.Equal(first.Rows, repeated.Rows);
        }
        using var finalFile = File.OpenRead(paths.DatabasePath);
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(finalFile, deadline.Token)));
        Directory.CreateDirectory(task);
        await File.WriteAllTextAsync(Path.Combine(task, "large-ui-query-measurement.json"), JsonSerializer.Serialize(new
        {
            Scope = "Read-only indexed UI query port; existing disposable million-entry database, no protected benchmark writes or indexing",
            Roots = catalogs.Count, Samples = samples, DatabaseSha256 = before,
            PeakProcessWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
            ProcessWorkingSetBytes = Environment.WorkingSet,
            Cancellation = "Pre-cancelled real-reader request refused within five seconds; VM mid-request cancellation covered separately",
            Limit = "One host observation per root, not reference-host acceptance or whole-catalogue search qualification",
        }, new JsonSerializerOptions { WriteIndented = true }), deadline.Token);
    }

    private static InventorySnapshot Snapshot(string root, params string[] names) => new(root,
        names.Select(name => new InventoryEntry(name, InventoryEntryKind.File, root + ":" + name, 7, Epoch, 128)).ToArray());

    // Ordinary read-only SQLite connections participate in WAL coordination and may
    // create these two native sidecars. Persistent database, backup and every other
    // file remain hash-protected; immutable mode would incorrectly ignore a live WAL.
    private static string[] FingerprintFiles(string root) => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .Where(path => !StringComparer.OrdinalIgnoreCase.Equals(path, Path.Combine(root, "inventory.db-wal"))
            && !StringComparer.OrdinalIgnoreCase.Equals(path, Path.Combine(root, "inventory.db-shm")))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();

    private static object? Scalar(AppDataPaths paths, string sql)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        { DataSource = paths.DatabasePath, Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand(); command.CommandText = sql;
        return command.ExecuteScalar();
    }
}

public sealed class IndexedUiLargeFactAttribute : FactAttribute
{
    public IndexedUiLargeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FO_RUN_INDEX_UI") != "1")
            Skip = "Prepared disposable million-row UI profile requires explicit FO_RUN_INDEX_UI=1 opt-in; no indexing is performed.";
        else if (typeof(IndexedInventoryQueryTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                     .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration != "Release")
            Skip = "Prepared million-row UI profile measurement requires the reviewed Release build.";
    }
}
