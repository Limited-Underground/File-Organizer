using FileOrganizer.Application;
using FileOrganizer.Desktop;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventorySearchViewModelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task ApplyAndClearResetPagingAndHistoryWhileRefreshAndSelectionKeepAppliedFilter()
    {
        var requests = new List<IndexedInventoryRequest>();
        var reader = new Reader { Query = (request, _) => { requests.Add(request); return Task.FromResult(Page(request)); } };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.NextPageAsync();
        Assert.True(model.CanPreviousPage);
        var filter = new IndexedInventoryFilter("needle", IndexedInventorySearchScope.Path, InventoryEntryKind.File);
        await model.ApplyFilterAsync(filter);
        Assert.Equal(filter, model.AppliedFilter);
        Assert.Equal(1, model.PageNumber);
        Assert.False(model.CanPreviousPage);
        Assert.Null(requests[^1].After);
        Assert.Equal(filter, requests[^1].Filter);
        await model.NextPageAsync();
        Assert.Equal(filter, requests[^1].After?.Filter);
        await model.SelectRootAsync("second");
        Assert.Equal(filter, requests[^1].Filter);
        Assert.Null(requests[^1].After);
        await model.RefreshAsync();
        Assert.Equal(filter, requests[^1].Filter);
        await model.ClearFilterAsync();
        Assert.Equal(new IndexedInventoryFilter(), model.AppliedFilter);
        Assert.False(model.CanPreviousPage);
        Assert.Null(requests[^1].After);
    }

    [Fact]
    public async Task LatestAppliedFilterWinsWhenOlderQueryIgnoresCancellation()
    {
        var old = Pending<IndexedInventoryPage>();
        var reader = new Reader { Query = (request, _) => request.Filter?.Text == "old" ? old.Task : Task.FromResult(Page(request)) };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        var earlier = model.ApplyFilterAsync(new("old"));
        Assert.True(model.IsLoading);
        Assert.Empty(model.Rows);
        await model.ApplyFilterAsync(new("new"));
        old.SetResult(Page(new("first", "generation-first", Filter: new("old"))));
        await earlier.WaitAsync(Deadline);
        Assert.Equal("new", model.AppliedFilter.Text);
        Assert.Equal("new", Assert.Single(model.Rows).RelativePath);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task InvalidFilterDoesNotQueryOrReplaceAppliedRowsAndCursor()
    {
        var calls = 0;
        var reader = new Reader { Query = (request, _) => { calls++; return Task.FromResult(Page(request)); } };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.ApplyFilterAsync(new("valid"));
        var before = calls;
        var rows = model.Rows;
        await model.ApplyFilterAsync(new(new string('x', 129)));
        Assert.Equal(before, calls);
        Assert.Equal("valid", model.AppliedFilter.Text);
        Assert.Equal(rows, model.Rows);
        Assert.True(model.CanNextPage);
        Assert.Equal("inventory.query_invalid_filter", model.ErrorCode);
        await model.NextPageAsync();
        Assert.Equal(before + 1, calls);
    }

    [Fact]
    public async Task ThirdRejectedApplyCannotRelabelRowsOrChangeTheAdmittedFilter()
    {
        var first = Pending<IndexedInventoryPage>();
        var second = Pending<IndexedInventoryPage>();
        var calls = 0;
        var reader = new Reader
        {
            Query = (request, _) => { calls++; return request.Filter?.Text switch { "one" => first.Task, "two" => second.Task, _ => Task.FromResult(Page(request)) }; },
        };
        var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        var one = model.ApplyFilterAsync(new("one"));
        var two = model.ApplyFilterAsync(new("two"));
        try
        {
            var before = calls;
            await model.ApplyFilterAsync(new("rejected"));
            Assert.Equal(before, calls);
            Assert.Equal("two", model.AppliedFilter.Text);
            Assert.Empty(model.Rows);
            Assert.Equal("inventory.request_limit", model.ErrorCode);
        }
        finally
        {
            first.TrySetResult(Page(new("first", "generation-first", Filter: new("one"))));
            second.TrySetResult(Page(new("first", "generation-first", Filter: new("two"))));
            await Task.WhenAll(one, two).WaitAsync(Deadline);
            await model.DisposeAsync();
        }
    }

    [Fact]
    public async Task EquivalentDraftPreservesPageHistoryButChangedCriteriaResetIt()
    {
        var requests = new List<IndexedInventoryRequest>();
        var reader = new Reader { Query = (request, _) => { requests.Add(request); return Task.FromResult(Page(request)); } };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.ApplyDraftAsync(new(MinBytes: "01", ModifiedFrom: "2024-02-29", CaseMode: IndexedInventoryCaseMode.MatchCase));
        await model.NextPageAsync();
        var cursor = requests[^1].After;
        await model.ApplyDraftAsync(new(MinBytes: "1", ModifiedFrom: "2024-02-29", CaseMode: IndexedInventoryCaseMode.MatchCase));
        Assert.Equal(2, model.PageNumber);
        Assert.True(model.CanPreviousPage);
        Assert.Equal(cursor, requests[^1].After);
        await model.ApplyDraftAsync(new(MinBytes: "2"));
        Assert.Equal(1, model.PageNumber);
        Assert.False(model.CanPreviousPage);
        Assert.Null(requests[^1].After);
    }

    [Theory]
    [InlineData("-1", "")]
    [InlineData("9223372036854775808", "")]
    [InlineData("1", "2024-02-30")]
    public async Task InvalidDraftKeepsAppliedRowsAndPaging(string bytes, string date)
    {
        var calls = 0;
        var reader = new Reader { Query = (request, _) => { calls++; return Task.FromResult(Page(request)); } };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.NextPageAsync();
        var previousRows = model.Rows;
        var previousCalls = calls;
        await model.ApplyDraftAsync(new(MinBytes: bytes, ModifiedFrom: date));
        Assert.Equal(previousCalls, calls);
        Assert.Same(previousRows, model.Rows);
        Assert.Equal(2, model.PageNumber);
        Assert.True(model.CanPreviousPage);
        Assert.Equal("inventory.query_invalid_filter", model.ErrorCode);
        Assert.Contains("yyyy-MM-dd", model.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReapplyingFilterAfterFailedOrCancelledFirstReadStartsPageOne(bool cancel)
    {
        var requests = new List<IndexedInventoryRequest>();
        var blocked = Pending<IndexedInventoryPage>();
        var failFirst = true;
        var reader = new Reader
        {
            Query = (request, _) =>
            {
                requests.Add(request);
                if (request.Filter?.Text == "changed" && failFirst)
                {
                    failFirst = false;
                    return cancel ? blocked.Task : Task.FromException<IndexedInventoryPage>(new InventoryIndexException("inventory.query_time_limit"));
                }
                return Task.FromResult(Page(request));
            },
        };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.NextPageAsync();
        var first = model.ApplyFilterAsync(new("changed"));
        if (cancel)
        {
            model.Cancel();
            blocked.SetResult(Page(requests[^1]));
        }
        await first.WaitAsync(Deadline);
        Assert.Equal(0, model.PageNumber);
        Assert.Empty(model.Rows);
        await model.ApplyFilterAsync(new("changed"));
        Assert.Equal(1, model.PageNumber);
        Assert.False(model.CanPreviousPage);
        Assert.Null(requests[^1].After);
        Assert.Equal("changed", Assert.Single(model.Rows).RelativePath);
        Assert.Null(model.ErrorCode);
    }

    [Fact]
    public async Task ExtensionOnlyNoMatchRemainsAFilteredResultAndClearRemovesItsSummary()
    {
        var reader = new Reader { Query = (request, _) => Task.FromResult(new IndexedInventoryPage(
            new(request.RootIdentity, request.GenerationId, 100, false), [], null, true, request.GenerationId)) };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        await model.ApplyFilterAsync(new(Extension: ".txt"));
        Assert.Contains("No saved indexed results match", model.Message);
        Assert.Contains("Filename suffix: .txt", model.FilterSummary);
        await model.ClearFilterAsync();
        Assert.Contains("published catalog has no indexed entries", model.Message);
        Assert.Contains("Filename suffix: Any", model.FilterSummary);
    }

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IndexedInventoryPage Page(IndexedInventoryRequest request) => new(
        new(request.RootIdentity, request.GenerationId, 100, false),
        [new(request.Filter?.Text is { Length: > 0 } text ? text : "unfiltered", InventoryEntryKind.File, 1,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), null)],
        request.After is null ? new(request.RootIdentity, request.GenerationId, request.PageSize, "after", request.Filter) : null,
        true, request.GenerationId);

    private sealed class Reader : IIndexedInventoryReader
    {
        public Func<IndexedInventoryRequest, CancellationToken, Task<IndexedInventoryPage>> Query { get; set; }
            = (request, _) => Task.FromResult(Page(request));
        public Task<IndexedCatalogPage> ReadCatalogsAsync(IndexedCatalogRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new IndexedCatalogPage([new("first", "generation-first", 100, false), new("second", "generation-second", 100, false)], null));
        public Task<IndexedInventoryPage> ReadPageAsync(IndexedInventoryRequest request, CancellationToken cancellationToken) => Query(request, cancellationToken);
    }
}
