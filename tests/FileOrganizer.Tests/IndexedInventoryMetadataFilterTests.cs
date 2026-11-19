using System.Globalization;
using System.Security.Cryptography;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventoryMetadataFilterTests
{
    private static readonly DateTime Day = new(2020, 2, 29, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(0, 0, "zero.txt")]
    [InlineData(10, 10, "folder/Mixed%_O'Brien.txt")]
    [InlineData(11, long.MaxValue, "last.txt")]
    [InlineData(0, long.MaxValue, "folder/Mixed%_O'Brien.txt,last.txt,zero.txt")]
    public async Task InclusiveSizeBoundsOnlyMatchFilesWithUsableMetadata(long minimum, long maximum, string expected)
    {
        var sample = await Sample();
        await Verify(sample, new(MinBytes: minimum, MaxBytes: maximum), expected.Split(','));
    }

    [Fact]
    public async Task InclusiveUtcCalendarDaysIncludeLastTickAndExcludeAdjacentDays()
    {
        var sample = await Sample();
        await Verify(sample, new(ModifiedFrom: new(2020, 2, 29), ModifiedThrough: new(2020, 2, 29)),
            ["folder", "folder/Mixed%_O'Brien.txt", "zero.txt"]);
        await Verify(sample, new(ModifiedFrom: new(2020, 3, 1)), ["last.txt"]);
        await Verify(sample, new(ModifiedThrough: new(2020, 2, 28)), []);
        await Verify(sample, new(ModifiedFrom: DateOnly.MinValue, ModifiedThrough: DateOnly.MaxValue),
            ["folder", "folder/Mixed%_O'Brien.txt", "last.txt", "zero.txt"]);
    }

    [Theory]
    [InlineData("MIXED%_O'", IndexedInventoryCaseMode.IgnoreCase, true)]
    [InlineData("MIXED%_O'", IndexedInventoryCaseMode.MatchCase, false)]
    [InlineData("Mixed%_O'", IndexedInventoryCaseMode.MatchCase, true)]
    [InlineData("MixedAAO'", IndexedInventoryCaseMode.IgnoreCase, false)]
    [InlineData("\\", IndexedInventoryCaseMode.IgnoreCase, false)]
    public async Task CombinedTextKindSizeDateAndCaseUseAndAndLiteralComparison(string text, IndexedInventoryCaseMode mode, bool match)
    {
        var sample = await Sample();
        await Verify(sample, new(text, IndexedInventorySearchScope.Path, InventoryEntryKind.File, 10, 10,
            new(2020, 2, 29), new(2020, 2, 29), mode, IndexedInventoryMetadataMode.Available),
            match ? ["folder/Mixed%_O'Brien.txt"] : []);
    }

    [Fact]
    public async Task AvailabilityDoesNotInventSizesOrDatesForLinksOrErroredRecords()
    {
        var sample = await Sample();
        await Verify(sample, new(Metadata: IndexedInventoryMetadataMode.Unavailable), ["link"]);
        // Fault injection is restricted to this newly minted metadata fixture, never indexed source files.
        Execute(sample.Paths, "UPDATE InventoryDirectoryEntry SET ErrorCode='filesystem.access_denied' WHERE RelativePath='zero.txt';");
        await Verify(sample, new(Metadata: IndexedInventoryMetadataMode.Unavailable), ["link", "zero.txt"]);
        await Verify(sample, new(MinBytes: 0, MaxBytes: 0), []);
        await Verify(sample, new(Kind: InventoryEntryKind.Directory, MinBytes: 0), []);
        await Verify(sample, new(Metadata: IndexedInventoryMetadataMode.Unavailable, ModifiedFrom: DateOnly.MinValue), []);
        var page = await new SqliteIndexedInventoryReader(sample.Paths).ReadPageAsync(new("metadata-root", sample.Generation), CancellationToken.None);
        Assert.All(page.Rows.Where(row => row.RelativePath is "link" or "zero.txt"), row =>
        {
            Assert.False(row.HasUsableMetadata);
            Assert.False(row.HasSize);
        });
        Assert.False(page.Rows.Single(row => row.Kind == InventoryEntryKind.Directory).HasSize);
    }

    [Fact]
    public void InvalidBoundsDatesAndEnumsRejectBeforeQuerying()
    {
        foreach (var invalid in new IndexedInventoryFilter[]
        {
            new(MinBytes: -1), new(MaxBytes: -1), new(MinBytes: 10, MaxBytes: 9),
            new(ModifiedFrom: new(2020, 3, 1), ModifiedThrough: new(2020, 2, 29)),
            new(CaseMode: (IndexedInventoryCaseMode)99), new(Metadata: (IndexedInventoryMetadataMode)99),
        })
            Assert.Equal("inventory.query_invalid_filter", Assert.Throws<InventoryIndexException>(() => IndexedInventoryFilter.Normalize(invalid)).Code);
    }

    [Theory]
    [InlineData("Length='not-a-number'")]
    [InlineData("ModifiedUtc='not-a-date'")]
    [InlineData("ModifiedUtc='2020-02-29T00:00:00.0000000'")]
    [InlineData("Kind=9")]
    public async Task MalformedStorageFailsTypedAndCannotBecomeAFalseEmptyOrZeroMatch(string mutation)
    {
        var sample = await Sample();
        Execute(sample.Paths, "UPDATE InventoryDirectoryEntry SET " + mutation + " WHERE RelativePath='zero.txt';");
        var error = await Assert.ThrowsAsync<InventoryIndexException>(() => new SqliteIndexedInventoryReader(sample.Paths).ReadPageAsync(
            new("metadata-root", sample.Generation, Filter: new("no-such-name", MinBytes: 0, MaxBytes: 0)), CancellationToken.None));
        Assert.Equal("inventory.query_invalid_metadata", error.Code);
    }

    [Fact]
    public async Task CursorsBindEveryMetadataAndCasePredicateAndPagingIsStable()
    {
        var sample = await Sample();
        var filter = new IndexedInventoryFilter(Kind: InventoryEntryKind.File, MinBytes: 0, MaxBytes: long.MaxValue,
            ModifiedFrom: DateOnly.MinValue, ModifiedThrough: DateOnly.MaxValue, Metadata: IndexedInventoryMetadataMode.Available);
        var reader = new SqliteIndexedInventoryReader(sample.Paths);
        var first = await reader.ReadPageAsync(new("metadata-root", sample.Generation, 1, Filter: filter), CancellationToken.None);
        Assert.NotNull(first.NextCursor);
        foreach (var changed in new[] { filter with { MinBytes = 1 }, filter with { MaxBytes = 10 },
            filter with { ModifiedFrom = new(2020, 2, 29) }, filter with { ModifiedThrough = new(2020, 3, 1) },
            filter with { CaseMode = IndexedInventoryCaseMode.MatchCase }, filter with { Metadata = IndexedInventoryMetadataMode.Any } })
            Assert.Equal("inventory.query_cursor_mismatch", (await Assert.ThrowsAsync<InventoryIndexException>(() => reader.ReadPageAsync(
                new("metadata-root", sample.Generation, 1, first.NextCursor, changed), CancellationToken.None))).Code);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var paths = new List<string>();
            IndexedInventoryCursor? cursor = null;
            do
            {
                var page = await reader.ReadPageAsync(new("metadata-root", sample.Generation, 1, cursor, filter), CancellationToken.None);
                paths.AddRange(page.Rows.Select(row => row.RelativePath)); cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(new[] { "folder/Mixed%_O'Brien.txt", "last.txt", "zero.txt" }, paths);
        }
    }

    [Fact]
    public async Task FilteredOldGenerationRemainsPinnedAndDisclosesInterruptedAttempt()
    {
        var sample = await Sample();
        var store = new SqliteInventoryStore(sample.Paths);
        var latest = await store.IndexAsync(new("metadata-root", [Entry("new.txt", 8, Day)]), 1, CancellationToken.None);
        var incomplete = await store.IndexAsync(new("metadata-root",
            [new("denied", InventoryEntryKind.File, "", 0, Day, 128, "filesystem.access_denied")]), 1, CancellationToken.None);
        Assert.False(incomplete.Published);
        var page = await new SqliteIndexedInventoryReader(sample.Paths).ReadPageAsync(new("metadata-root", sample.Generation,
            Filter: new(MinBytes: 10, MaxBytes: 10, ModifiedFrom: new(2020, 2, 29), ModifiedThrough: new(2020, 2, 29))), CancellationToken.None);
        Assert.Equal("folder/Mixed%_O'Brien.txt", Assert.Single(page.Rows).RelativePath);
        Assert.False(page.IsCurrentPublication);
        Assert.Equal(latest.GenerationId, page.CurrentPublishedGenerationId);
        Assert.True(page.Catalog.HasRetainedUnpublishedAttempts);
        Assert.False(page.Catalog.IsAtomicSnapshot);
        Assert.False(page.Catalog.LiveAvailabilityKnown);
    }

    private static async Task Verify((AppDataPaths Paths, string Generation) sample, IndexedInventoryFilter filter, string[] expected)
    {
        var before = Hash(sample.Paths);
        var page = await new SqliteIndexedInventoryReader(sample.Paths).ReadPageAsync(new("metadata-root", sample.Generation, Filter: filter), CancellationToken.None);
        Assert.Equal(expected, page.Rows.Select(row => row.RelativePath));
        Assert.Equal(before, Hash(sample.Paths));
    }

    private static async Task<(AppDataPaths Paths, string Generation)> Sample()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var result = await new SqliteInventoryStore(paths).IndexAsync(new("metadata-root",
        [new("folder", InventoryEntryKind.Directory, "folder-id", 0, Day, 16),
            Entry("zero.txt", 0, Day), Entry("folder/Mixed%_O'Brien.txt", 10, Day.AddDays(1).AddTicks(-1)),
            Entry("last.txt", long.MaxValue, Day.AddDays(1)),
            new("link", InventoryEntryKind.ExcludedReparse, "", 0, Day, (long)FileAttributes.ReparsePoint)]), 2, CancellationToken.None);
        Assert.True(result.Published);
        return (paths, result.GenerationId);
    }
    private static InventoryEntry Entry(string name, long size, DateTime modified) => new(name, InventoryEntryKind.File,
        "id-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name))), size, modified, 128);
    private static string Hash(AppDataPaths paths) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.DatabasePath)));
    private static void Execute(AppDataPaths paths, string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
}
