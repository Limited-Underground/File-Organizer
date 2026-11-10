// T-0041: no-window draft/admission/query composition against a fresh disposable SQLite fixture.
using FileOrganizer.Application;
using FileOrganizer.Desktop;
using FileOrganizer.Persistence;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class T0041InventoryExtensionDraftTests
{
    [Fact]
    public async Task ExtensionDraftReachesRealQueryAndRetainsPagingAndExactCase()
    {
        var reader = await Sample();
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.ApplyDraftAsync(new(Extension: ".txt"));
        Assert.Equal(".txt", model.AppliedFilter.Extension);
        Assert.Equal(50, model.Rows.Count);
        Assert.Contains("Filename suffix: .txt", model.FilterSummary);
        Assert.All(model.Rows, row => Assert.EndsWith(".txt", row.RelativePath, StringComparison.OrdinalIgnoreCase));
        var first = model.Rows.Select(row => row.RelativePath).ToArray();
        await model.NextPageAsync();
        Assert.Equal(2, model.PageNumber);
        Assert.Equal(10, model.Rows.Count);
        Assert.Empty(first.Intersect(model.Rows.Select(row => row.RelativePath)));
        Assert.False(model.CanNextPage);
        await model.ApplyDraftAsync(new(Extension: ".TXT", CaseMode: IndexedInventoryCaseMode.MatchCase));
        Assert.Equal(1, model.PageNumber);
        Assert.False(model.CanPreviousPage);
        Assert.Equal("file-020.TXT", Assert.Single(model.Rows).RelativePath);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("path")]
    [InlineData("over-limit")]
    public async Task RejectedExtensionDraftNeverQueriesOrDiscardsAppliedRowsAndHistory(string fault)
    {
        var reader = await Sample();
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.ApplyDraftAsync(new(Extension: ".txt"));
        await model.NextPageAsync();
        var rows = model.Rows;
        var calls = reader.PageReads;
        var suffix = fault switch { "null" => null, "path" => ".bad/path", _ => "." + new string('x', 128) };
        await model.ApplyDraftAsync(new(Extension: suffix!));
        Assert.Equal("inventory.query_invalid_filter", model.ErrorCode);
        Assert.Equal(calls, reader.PageReads);
        Assert.Same(rows, model.Rows);
        Assert.Equal(".txt", model.AppliedFilter.Extension);
        Assert.Equal(2, model.PageNumber);
        Assert.True(model.CanPreviousPage);
    }

    [Fact]
    public async Task DraftExtensionComposesWithExistingMetadataTextAndCaseAndClearRemovesIt()
    {
        var reader = await Sample();
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.ApplyDraftAsync(new("file-020", Kind: InventoryEntryKind.File, MinBytes: "9", MaxBytes: "9",
            ModifiedFrom: "2020-01-01", ModifiedThrough: "2020-01-01", CaseMode: IndexedInventoryCaseMode.MatchCase,
            Metadata: IndexedInventoryMetadataMode.Available, Extension: ".TXT"));
        Assert.Equal("file-020.TXT", Assert.Single(model.Rows).RelativePath);
        Assert.Equal(9L, model.AppliedFilter.MinBytes);
        Assert.Equal(new DateOnly(2020, 1, 1), model.AppliedFilter.ModifiedFrom);
        await model.ClearFilterAsync();
        Assert.Equal(new IndexedInventoryFilter(), model.AppliedFilter);
        Assert.Contains("Filename suffix: Any", model.FilterSummary);
        Assert.Equal(1, model.PageNumber);
        Assert.Equal(50, model.Rows.Count);
        Assert.True(model.CanNextPage);
        Assert.Null(model.ErrorCode);
    }

    private static async Task<CountedReader> Sample()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var epoch = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var files = Enumerable.Range(0, 60).Select(index => new InventoryEntry(
            $"file-{index:D3}." + (index == 20 ? "TXT" : "txt"), InventoryEntryKind.File, "id-" + index, 9, epoch, 128)).ToList();
        files.Add(new("other.gz", InventoryEntryKind.File, "id-other", 7, epoch, 128));
        var result = await new SqliteInventoryStore(paths).IndexAsync(new("draft-extension-root", files), 16, CancellationToken.None);
        Assert.True(result.Published);
        return new(new SqliteIndexedInventoryReader(paths));
    }

    private sealed class CountedReader(IIndexedInventoryReader inner) : IIndexedInventoryReader
    {
        public int PageReads { get; private set; }
        public Task<IndexedCatalogPage> ReadCatalogsAsync(IndexedCatalogRequest request, CancellationToken token)
            => inner.ReadCatalogsAsync(request, token);
        public Task<IndexedInventoryPage> ReadPageAsync(IndexedInventoryRequest request, CancellationToken token)
        {
            PageReads++;
            return inner.ReadPageAsync(request, token);
        }
    }
}
