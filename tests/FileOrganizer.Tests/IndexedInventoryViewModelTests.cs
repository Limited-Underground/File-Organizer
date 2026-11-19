using FileOrganizer.Application;
using FileOrganizer.Desktop;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventoryViewModelTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task LatestRefreshWinsWhenSupersededQueryIgnoresCancellation()
    {
        var old = Pending<IndexedCatalogPage>();
        var calls = 0;
        var reader = new FakeReader
        {
            Catalogs = (_, _) => ++calls == 1 ? old.Task : Task.FromResult(CatalogPage("new")),
        };
        await using var model = new InventoryViewModel(reader);
        var previous = model.RefreshAsync();
        Assert.True(model.IsLoading);
        await model.RefreshAsync().WaitAsync(Deadline);
        Assert.Equal("new", model.SelectedCatalog?.RootIdentity);
        old.SetResult(CatalogPage("old"));
        await previous.WaitAsync(Deadline);
        Assert.Equal("new", model.SelectedCatalog?.RootIdentity);
        Assert.Equal("new-file", Assert.Single(model.Rows).RelativePath);
        Assert.Null(model.ErrorCode);
        Assert.False(model.IsLoading);
    }

    [Fact]
    public async Task OldPageFailureCannotOverwriteNewSelectionAndNeverLeaksExceptionMessage()
    {
        var old = Pending<IndexedInventoryPage>();
        var reader = new FakeReader
        {
            Catalogs = (_, _) => Task.FromResult(new IndexedCatalogPage([Catalog("a"), Catalog("b")], null)),
            Page = (request, _) => request.RootIdentity == "a" ? old.Task : Task.FromResult(Page("b")),
        };
        await using var model = new InventoryViewModel(reader);
        var first = model.RefreshAsync();
        Assert.True(model.IsLoading);
        await model.SelectRootAsync("b").WaitAsync(Deadline);
        old.SetException(new IOException("PRIVATE-PATH-DO-NOT-EXPOSE"));
        await first.WaitAsync(Deadline);
        Assert.Equal("b", model.SelectedCatalog?.RootIdentity);
        Assert.Equal("b-file", Assert.Single(model.Rows).RelativePath);
        Assert.Null(model.ErrorCode);
        Assert.DoesNotContain("PRIVATE", model.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelReturnsImmediatelyAndLatePageCannotReplaceDisplayedRows()
    {
        var delayed = Pending<IndexedInventoryPage>();
        var calls = 0;
        var reader = new FakeReader
        {
            Page = (_, _) => ++calls == 1 ? Task.FromResult(Page("root", next: true)) : delayed.Task,
        };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        var next = model.NextPageAsync();
        Assert.True(model.IsLoading);
        model.Cancel();
        Assert.False(model.IsLoading);
        Assert.Equal("root-file", Assert.Single(model.Rows).RelativePath);
        delayed.SetResult(Page("root", row: "late"));
        await next.WaitAsync(Deadline);
        Assert.Equal("root-file", Assert.Single(model.Rows).RelativePath);
        Assert.Contains("cancel", model.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposeJoinsAllOutstandingQueriesAndRejectsNewWork()
    {
        var delayed = Pending<IndexedCatalogPage>();
        var reader = new FakeReader { Catalogs = (_, _) => delayed.Task };
        var model = new InventoryViewModel(reader);
        var loading = model.RefreshAsync();
        var disposal = model.DisposeAsync().AsTask();
        Assert.False(disposal.IsCompleted);
        Assert.False(model.IsLoading);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => model.RefreshAsync());
        delayed.SetResult(CatalogPage("late"));
        await Task.WhenAll(loading, disposal).WaitAsync(Deadline);
        Assert.Empty(model.Rows);
        Assert.Empty(model.Catalogs);
        await model.DisposeAsync();
    }

    [Fact]
    public async Task RetainedOnlyCatalogNeverQueriesStagingAndEmptyCatalogIsHonest()
    {
        var rowCalls = 0;
        var reader = new FakeReader
        {
            Catalogs = (_, _) => Task.FromResult(new IndexedCatalogPage([new("retained", null, 0, true)], null)),
            Page = (_, _) => { rowCalls++; throw new InvalidOperationException("Staging must not be queried."); },
        };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        Assert.Equal(0, rowCalls);
        Assert.Empty(model.Rows);
        Assert.Contains("no published generation", model.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(model.CanNextPage);
        reader.Catalogs = (_, _) => Task.FromResult(new IndexedCatalogPage([], null));
        await model.RefreshAsync();
        Assert.Null(model.SelectedCatalog);
        Assert.Contains("No saved", model.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PinnedStalePublicationNavigatesBackWithoutMixingGenerationsAndRefreshResets()
    {
        var requests = new List<IndexedInventoryRequest>();
        var generation = "first";
        var reader = new FakeReader
        {
            Catalogs = (_, _) => Task.FromResult(new IndexedCatalogPage([new("root", generation, 4, false)], null)),
            Page = (request, _) =>
            {
                requests.Add(request);
                var catalog = new IndexedCatalog("root", request.GenerationId, 4, false);
                return Task.FromResult(new IndexedInventoryPage(catalog,
                    [new(request.After is null ? "a" : "b", InventoryEntryKind.File, 1, Epoch, null)],
                    request.After is null ? new("root", request.GenerationId, 50, "a") : null,
                    request.GenerationId == generation, generation));
            },
        };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        generation = "second";
        await model.NextPageAsync();
        Assert.True(model.HasPublicationChanged);
        Assert.Equal("first", model.SelectedCatalog?.GenerationId);
        Assert.Equal("b", Assert.Single(model.Rows).RelativePath);
        await model.PreviousPageAsync();
        Assert.Equal("a", Assert.Single(model.Rows).RelativePath);
        Assert.All(requests, request => Assert.Equal("first", request.GenerationId));
        await model.RefreshAsync();
        Assert.Equal("second", model.SelectedCatalog?.GenerationId);
        Assert.False(model.HasPublicationChanged);
        Assert.False(model.CanPreviousPage);
    }

    [Fact]
    public async Task ActiveQueryFailureShowsOnlySafeCodeAndCanRecover()
    {
        var reader = new FakeReader { Catalogs = (_, _) => throw new IOException("secret personal filename") };
        await using var model = new InventoryViewModel(reader);
        await model.RefreshAsync();
        Assert.Equal("inventory.read_failed", model.ErrorCode);
        Assert.DoesNotContain("secret", model.Message, StringComparison.OrdinalIgnoreCase);
        reader.Catalogs = (_, _) => Task.FromResult(CatalogPage("recovered"));
        await model.RefreshAsync();
        Assert.Null(model.ErrorCode);
        Assert.Equal("recovered", model.SelectedCatalog?.RootIdentity);
    }

    [Fact]
    public async Task RepeatedCancelRefreshCannotExceedTwoNoncooperativeQueriesAndRecoversAdmission()
    {
        var first = Pending<IndexedCatalogPage>();
        var second = Pending<IndexedCatalogPage>();
        var calls = 0;
        var reader = new FakeReader
        {
            Catalogs = (_, _) => (++calls) switch
            {
                1 => first.Task,
                2 => second.Task,
                _ => Task.FromResult(CatalogPage("admitted")),
            },
        };
        var model = new InventoryViewModel(reader);
        var old = model.RefreshAsync();
        var current = model.RefreshAsync();
        try
        {
            for (var iteration = 0; iteration < 20; iteration++)
            {
                model.Cancel();
                await model.RefreshAsync().WaitAsync(Deadline);
            }
            Assert.Equal(2, calls);
            first.SetResult(CatalogPage("stale"));
            await old.WaitAsync(Deadline);
            await model.RefreshAsync().WaitAsync(Deadline);
            Assert.Equal(3, calls);
            Assert.Equal("admitted", model.SelectedCatalog?.RootIdentity);
            var disposing = model.DisposeAsync().AsTask();
            Assert.False(disposing.IsCompleted);
            second.SetResult(CatalogPage("too-late"));
            await Task.WhenAll(current, disposing).WaitAsync(Deadline);
            Assert.Equal("admitted", model.SelectedCatalog?.RootIdentity);
        }
        finally
        {
            first.TrySetResult(CatalogPage("cleanup"));
            second.TrySetResult(CatalogPage("cleanup"));
            await model.DisposeAsync().AsTask().WaitAsync(Deadline);
        }
    }

    [Fact]
    public async Task LoadingNotificationMayDisposeWithoutLaunchingQueryOrDeadlockingShutdown()
    {
        var calls = 0;
        var reader = new FakeReader
        {
            Catalogs = (_, _) => { calls++; return Task.FromResult(CatalogPage("forbidden")); },
        };
        var model = new InventoryViewModel(reader);
        Task? disposal = null;
        model.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(InventoryViewModel.IsLoading) && model.IsLoading && disposal is null)
                disposal = model.DisposeAsync().AsTask();
        };
        var refresh = model.RefreshAsync();
        Assert.NotNull(disposal);
        await Task.WhenAll(refresh, disposal!).WaitAsync(Deadline);
        Assert.Equal(0, calls);
        Assert.Empty(model.Catalogs);
        Assert.Empty(model.Rows);
        Assert.False(model.IsLoading);
        await model.DisposeAsync();
    }

    private static TaskCompletionSource<T> Pending<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static IndexedCatalog Catalog(string root) => new(root, root + "-generation", 2, false);
    private static IndexedCatalogPage CatalogPage(string root) => new([Catalog(root)], null);
    private static IndexedInventoryPage Page(string root, string? row = null, bool next = false) => new(Catalog(root),
        [new(row ?? root + "-file", InventoryEntryKind.File, 5, Epoch, null)],
        next ? new(root, root + "-generation", 50, root + "-file") : null, true, root + "-generation");

    private sealed class FakeReader : IIndexedInventoryReader
    {
        public Func<IndexedCatalogRequest, CancellationToken, Task<IndexedCatalogPage>> Catalogs { get; set; }
            = (_, _) => Task.FromResult(CatalogPage("root"));
        public Func<IndexedInventoryRequest, CancellationToken, Task<IndexedInventoryPage>> Page { get; set; }
            = (request, _) => Task.FromResult(IndexedInventoryViewModelTests.Page(request.RootIdentity));
        public Task<IndexedCatalogPage> ReadCatalogsAsync(IndexedCatalogRequest request, CancellationToken cancellationToken)
            => Catalogs(request, cancellationToken);
        public Task<IndexedInventoryPage> ReadPageAsync(IndexedInventoryRequest request, CancellationToken cancellationToken)
            => Page(request, cancellationToken);
    }
}
