using System.Diagnostics;
using System.Security.Cryptography;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventorySearchTests
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("%_", "literal%_name.txt")]
    [InlineData("'", "O'Brien.txt")]
    [InlineData("café", "CAFÉ.txt")]
    [InlineData("漢", "漢字.txt")]
    public async Task LiteralSearchUsesOrdinalIgnoreCaseWithoutSqlWildcardInterpretation(string text, string expected)
    {
        var (paths, generation) = await CreateSample();
        var page = await new SqliteIndexedInventoryReader(paths).ReadPageAsync(
            new("search-root", generation, Filter: new(text)), CancellationToken.None);
        Assert.Equal(expected, Assert.Single(page.Rows).RelativePath);
    }

    [Theory]
    [InlineData("' OR 1=1 --")]
    [InlineData("\\")]
    [InlineData("cafe\u0301")]
    [InlineData(" plain ")]
    public async Task InjectionBackslashDecomposedUnicodeAndWhitespaceRemainLiteral(string text)
    {
        var (paths, generation) = await CreateSample();
        var before = DatabaseHash(paths);
        var page = await new SqliteIndexedInventoryReader(paths).ReadPageAsync(
            new("search-root", generation, Filter: new(text)), CancellationToken.None);
        Assert.Empty(page.Rows);
        Assert.Equal(before, DatabaseHash(paths));
    }

    [Fact]
    public async Task NameAndPathScopeCombineWithKindWithoutExposingOtherRows()
    {
        var (paths, generation) = await CreateSample();
        var reader = new SqliteIndexedInventoryReader(paths);
        var name = await reader.ReadPageAsync(new("search-root", generation, Filter: new("folder")), CancellationToken.None);
        Assert.Equal("folder", Assert.Single(name.Rows).RelativePath);
        var path = await reader.ReadPageAsync(new("search-root", generation,
            Filter: new("folder", IndexedInventorySearchScope.Path, InventoryEntryKind.File)), CancellationToken.None);
        Assert.Equal("folder/plain.txt", Assert.Single(path.Rows).RelativePath);
        var reparse = await reader.ReadPageAsync(new("search-root", generation,
            Filter: new(Kind: InventoryEntryKind.ExcludedReparse)), CancellationToken.None);
        Assert.Equal("link", Assert.Single(reparse.Rows).RelativePath);
    }

    [Fact]
    public async Task SearchCursorBindsExactFilterIncludingCaseKindAndScopeWhileDefaultIsCanonical()
    {
        var (paths, generation) = await CreateSample();
        var reader = new SqliteIndexedInventoryReader(paths);
        var filter = new IndexedInventoryFilter("literal", IndexedInventorySearchScope.Name, InventoryEntryKind.File);
        var first = await reader.ReadPageAsync(new("search-root", generation, 1, Filter: filter), CancellationToken.None);
        Assert.NotNull(first.NextCursor);
        var next = await reader.ReadPageAsync(new("search-root", generation, 1, first.NextCursor, filter), CancellationToken.None);
        Assert.Empty(first.Rows.Select(row => row.RelativePath).Intersect(next.Rows.Select(row => row.RelativePath)));
        Assert.Null(next.NextCursor);
        foreach (var changed in new[] { filter with { Text = "LITERAL" }, filter with { Kind = null }, filter with { Scope = IndexedInventorySearchScope.Path } })
        {
            var error = await Assert.ThrowsAsync<InventoryIndexException>(() => reader.ReadPageAsync(
                new("search-root", generation, 1, first.NextCursor, changed), CancellationToken.None));
            Assert.Equal("inventory.query_cursor_mismatch", error.Code);
        }
        var unfiltered = await reader.ReadPageAsync(new("search-root", generation, 1), CancellationToken.None);
        var explicitEmpty = await reader.ReadPageAsync(new("search-root", generation, 1, unfiltered.NextCursor, new()), CancellationToken.None);
        Assert.NotEmpty(explicitEmpty.Rows);
    }

    [Fact]
    public async Task ActualSqlitePredicateCancellationAfterCandidatesNeverReturnsFalseEmptyPage()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var snapshot = new InventorySnapshot("cancel-root", Enumerable.Range(0, 128).Select(index =>
            File($"item-{index:D3}", "id-" + index)).ToArray());
        var generation = await new SqliteInventoryStore(paths).IndexAsync(snapshot, 16, CancellationToken.None);
        var before = DatabaseHash(paths);
        using var cancellation = new CancellationTokenSource();
        var observed = 0;
        var reader = new SqliteIndexedInventoryReader(paths, count =>
        {
            observed = count;
            if (count == 10) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadPageAsync(
            new("cancel-root", generation.GenerationId, Filter: new(Kind: InventoryEntryKind.Directory)), cancellation.Token));
        Assert.InRange(observed, 10, 128);
        Assert.Equal(before, DatabaseHash(paths));
    }

    [Fact]
    public void InvalidFiltersRejectBeforeReadingAndPreserveExactValidCodepoints()
    {
        Assert.Equal(new IndexedInventoryFilter(), IndexedInventoryFilter.Normalize(null));
        var valid = new IndexedInventoryFilter(" CAFE\u0301 %_\\' ", IndexedInventorySearchScope.Path, InventoryEntryKind.File);
        Assert.Equal(valid, IndexedInventoryFilter.Normalize(valid));
        foreach (var filter in new[]
        {
            new IndexedInventoryFilter(null!), new(new string('x', 129)), new("line\nbreak"),
            new("", (IndexedInventorySearchScope)123), new("", Kind: (InventoryEntryKind)123),
        })
        {
            var error = Assert.Throws<InventoryIndexException>(() => IndexedInventoryFilter.Normalize(filter));
            Assert.Equal("inventory.query_invalid_filter", error.Code);
        }
    }

    [Fact]
    public void ExpiredPredicateAndCandidateLimitThrowTypedFailuresInsteadOfNoMatch()
    {
        var expired = new IndexedFilterEvaluation(new("not-present"), CancellationToken.None,
            Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency, 0);
        var time = Assert.Throws<InventoryIndexException>(() => expired.Match("file", (long)InventoryEntryKind.File));
        Assert.Equal("inventory.query_time_limit", time.Code);
        Assert.Equal(time.Code, expired.FailureCode);
        var exhausted = new IndexedFilterEvaluation(new(Kind: InventoryEntryKind.Directory), CancellationToken.None,
            Stopwatch.GetTimestamp(), 1_100_000);
        var count = Assert.Throws<InventoryIndexException>(() => exhausted.Match("file", (long)InventoryEntryKind.File));
        Assert.Equal("inventory.query_scan_limit", count.Code);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var cancelledAndExpired = new IndexedFilterEvaluation(new("missing"), cancelled.Token,
            Stopwatch.GetTimestamp() - 6 * Stopwatch.Frequency, 1_100_000);
        Assert.ThrowsAny<OperationCanceledException>(() => cancelledAndExpired.Match("file", (long)InventoryEntryKind.File));
    }

    private static async Task<(AppDataPaths Paths, string Generation)> CreateSample()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var snapshot = new InventorySnapshot("search-root",
        [
            new("folder", InventoryEntryKind.Directory, "directory", 0, Epoch, 16),
            File("folder/plain.txt", "plain"), File("literal%_name.txt", "literal"), File("literalAAname.txt", "ordinary"),
            File("CAFÉ.txt", "accent"), File("漢字.txt", "cjk"), File("O'Brien.txt", "quote"),
            new("link", InventoryEntryKind.ExcludedReparse, "", 0, Epoch, (long)FileAttributes.ReparsePoint),
        ]);
        var indexed = await new SqliteInventoryStore(paths).IndexAsync(snapshot, 2, CancellationToken.None);
        return (paths, indexed.GenerationId);
    }

    private static InventoryEntry File(string path, string identity) => new(path, InventoryEntryKind.File, identity, 9, Epoch, 128);
    private static string DatabaseHash(AppDataPaths paths) => Convert.ToHexString(SHA256.HashData(System.IO.File.ReadAllBytes(paths.DatabasePath)));
}
