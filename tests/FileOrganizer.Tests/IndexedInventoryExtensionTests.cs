using System.Security.Cryptography;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventoryExtensionTests
{
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(".txt", IndexedInventoryCaseMode.IgnoreCase, "A.TXT,b.txt,folder/name.txt")]
    [InlineData(".txt", IndexedInventoryCaseMode.MatchCase, "b.txt,folder/name.txt")]
    [InlineData(".TXT", IndexedInventoryCaseMode.MatchCase, "A.TXT")]
    [InlineData(".tar.gz", IndexedInventoryCaseMode.IgnoreCase, "archive.tar.gz")]
    [InlineData(".%_'", IndexedInventoryCaseMode.IgnoreCase, "literal.%_'")]
    [InlineData(".gitignore", IndexedInventoryCaseMode.IgnoreCase, ".gitignore")]
    [InlineData(".café", IndexedInventoryCaseMode.IgnoreCase, "unicode.CAFÉ")]
    [InlineData(".cafe\u0301", IndexedInventoryCaseMode.IgnoreCase, "")]
    [InlineData(".txt ", IndexedInventoryCaseMode.IgnoreCase, "")]
    public async Task LiteralFilenameSuffixUsesExplicitCaseAndNeverParentDirectoryOrLinks(
        string extension, IndexedInventoryCaseMode mode, string expected)
    {
        var sample = await Sample();
        var before = Hash(sample.Paths);
        var page = await new SqliteIndexedInventoryReader(sample.Paths).ReadPageAsync(
            new("extension-root", sample.Generation, Filter: new(Extension: extension, CaseMode: mode)), CancellationToken.None);
        Assert.Equal(expected.Length == 0 ? [] : expected.Split(','), page.Rows.Select(row => row.RelativePath));
        Assert.Equal(before, Hash(sample.Paths));
        Assert.All(page.Rows, row => Assert.Equal(InventoryEntryKind.File, row.Kind));
        Assert.False(page.Catalog.LiveAvailabilityKnown);
        Assert.False(page.Catalog.IsAtomicSnapshot);
    }

    [Fact]
    public async Task ExtensionCombinesWithTextSizeDateKindAndMetadataRatherThanReplacingThem()
    {
        var sample = await Sample();
        var reader = new SqliteIndexedInventoryReader(sample.Paths);
        var filter = new IndexedInventoryFilter("name", IndexedInventorySearchScope.Name, InventoryEntryKind.File,
            9, 9, new(2020, 1, 1), new(2020, 1, 1), Metadata: IndexedInventoryMetadataMode.Available, Extension: ".txt");
        var page = await reader.ReadPageAsync(new("extension-root", sample.Generation, Filter: filter), CancellationToken.None);
        Assert.Equal("folder/name.txt", Assert.Single(page.Rows).RelativePath);
        var none = await reader.ReadPageAsync(new("extension-root", sample.Generation, Filter: filter with { MinBytes = 10, MaxBytes = 10 }), CancellationToken.None);
        Assert.Empty(none.Rows);
        var directories = await reader.ReadPageAsync(new("extension-root", sample.Generation,
            Filter: new(Kind: InventoryEntryKind.Directory, Extension: ".txt")), CancellationToken.None);
        Assert.Empty(directories.Rows);
    }

    [Fact]
    public async Task ExtensionCursorBindsExactSuffixCaseAndGenerationWithStableRepeatedPages()
    {
        var sample = await Sample();
        var reader = new SqliteIndexedInventoryReader(sample.Paths);
        var filter = new IndexedInventoryFilter(Extension: ".txt");
        var first = await reader.ReadPageAsync(new("extension-root", sample.Generation, 1, Filter: filter), CancellationToken.None);
        Assert.NotNull(first.NextCursor);
        foreach (var changed in new[] { filter with { Extension = "" }, filter with { Extension = ".TXT" },
            filter with { Extension = ".gz" }, filter with { CaseMode = IndexedInventoryCaseMode.MatchCase } })
            Assert.Equal("inventory.query_cursor_mismatch", (await Assert.ThrowsAsync<InventoryIndexException>(() =>
                reader.ReadPageAsync(new("extension-root", sample.Generation, 1, first.NextCursor, changed), CancellationToken.None))).Code);
        for (var repeat = 0; repeat < 2; repeat++)
        {
            var paths = new List<string>();
            IndexedInventoryCursor? cursor = null;
            do
            {
                var page = await reader.ReadPageAsync(new("extension-root", sample.Generation, 1, cursor, filter), CancellationToken.None);
                paths.AddRange(page.Rows.Select(row => row.RelativePath)); cursor = page.NextCursor;
            } while (cursor is not null);
            Assert.Equal(new[] { "A.TXT", "b.txt", "folder/name.txt" }, paths);
        }
    }

    [Fact]
    public async Task InvalidExtensionsRefuseBeforeAnyDatabaseReadAndDefaultStaysInactive()
    {
        Assert.False(new IndexedInventoryFilter().IsActive);
        Assert.True(new IndexedInventoryFilter(Extension: ".txt").IsActive);
        var reader = new SqliteIndexedInventoryReader(new AppDataPaths(new TestWorkspace().Root));
        foreach (var invalid in new string?[] { null, ".", "txt", ".bad/path", ".bad\\path", ".bad\n", "." + new string('x', 128) })
            Assert.Equal("inventory.query_invalid_filter", (await Assert.ThrowsAsync<InventoryIndexException>(() => reader.ReadPageAsync(
                new("extension-root", Guid.NewGuid().ToString("N"), Filter: new(Extension: invalid!)), CancellationToken.None))).Code);
    }

    [Fact]
    public async Task ExtensionOnlyQueryRetainsCancellationAndMalformedMetadataRefusal()
    {
        var sample = await Sample();
        using var cancellation = new CancellationTokenSource();
        var reader = new SqliteIndexedInventoryReader(sample.Paths, count => { if (count == 2) cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadPageAsync(
            new("extension-root", sample.Generation, Filter: new(Extension: ".missing")), cancellation.Token));
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = sample.Paths.DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "UPDATE InventoryDirectoryEntry SET Length='bad' WHERE RelativePath='b.txt';";
        command.ExecuteNonQuery();
        Assert.Equal("inventory.query_invalid_metadata", (await Assert.ThrowsAsync<InventoryIndexException>(() =>
            new SqliteIndexedInventoryReader(sample.Paths).ReadPageAsync(new("extension-root", sample.Generation,
                Filter: new(Extension: ".missing")), CancellationToken.None))).Code);
    }

    [Fact]
    public void ExtensionOnlyEvaluationRetainsExistingTimeAndCandidateLimits()
    {
        var filter = new IndexedInventoryFilter(Extension: ".txt");
        var expired = new IndexedFilterEvaluation(filter, CancellationToken.None,
            System.Diagnostics.Stopwatch.GetTimestamp() - 6 * System.Diagnostics.Stopwatch.Frequency, 0);
        Assert.Equal("inventory.query_time_limit", Assert.Throws<InventoryIndexException>(() => expired.Match("a.txt", 1)).Code);
        var exhausted = new IndexedFilterEvaluation(filter, CancellationToken.None,
            System.Diagnostics.Stopwatch.GetTimestamp(), 1_100_000);
        Assert.Equal("inventory.query_scan_limit", Assert.Throws<InventoryIndexException>(() => exhausted.Match("a.txt", 1)).Code);
    }

    private static async Task<(AppDataPaths Paths, string Generation)> Sample()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var names = new[] { "A.TXT", "b.txt", "archive.tar.gz", "literal.%_'", ".gitignore", "unicode.CAFÉ", "plain", "folder/name.txt", "folder.txt/plain" };
        var entries = names.Select((name, index) => new InventoryEntry(name, InventoryEntryKind.File, "id-" + index, 9, Epoch, 128)).ToList();
        entries.Add(new("folder", InventoryEntryKind.Directory, "folder", 0, Epoch, 16));
        entries.Add(new("folder.txt", InventoryEntryKind.Directory, "folder.txt", 0, Epoch, 16));
        entries.Add(new("link.txt", InventoryEntryKind.ExcludedReparse, "", 0, Epoch, (long)FileAttributes.ReparsePoint));
        var result = await new SqliteInventoryStore(paths).IndexAsync(new("extension-root", entries), 2, CancellationToken.None);
        Assert.True(result.Published);
        return (paths, result.GenerationId);
    }

    private static string Hash(AppDataPaths paths) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(paths.DatabasePath)));
}
