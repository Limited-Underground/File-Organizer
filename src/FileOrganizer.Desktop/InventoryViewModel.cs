using System.ComponentModel;
using System.Runtime.CompilerServices;
using FileOrganizer.Application;

namespace FileOrganizer.Desktop;

/// <summary>Bounded saved-result navigation. It has no filesystem or indexing dependency.</summary>
internal sealed class InventoryViewModel(IIndexedInventoryReader reader) : INotifyPropertyChanged, IAsyncDisposable
{
    private const int PageSize = 50;
    private const int HistoryLimit = 512;
    private readonly HashSet<Task> pending = [];
    private readonly List<IndexedInventoryCursor?> pageHistory = [];
    private readonly List<string?> catalogHistory = [];
    private CancellationTokenSource? active;
    private long version;
    private bool disposed;
    private bool isLoading;
    private IReadOnlyList<IndexedCatalog> catalogs = [];
    private IReadOnlyList<IndexedInventoryRow> rows = [];
    private IndexedCatalog? selectedCatalog;
    private IndexedInventoryCursor? pageAfter;
    private IndexedInventoryCursor? nextCursor;
    private string? catalogAfter;
    private string? nextCatalogAfter;
    private string message = "Choose Inventory to read saved indexed results.";
    private string? errorCode;
    private bool publicationChanged;
    private int pageNumber;
    private Task? disposal;
    private int inFlight;
    private IndexedInventoryFilter appliedFilter = new();

    public event PropertyChangedEventHandler? PropertyChanged;
    public IReadOnlyList<IndexedCatalog> Catalogs => catalogs;
    public IReadOnlyList<IndexedInventoryRow> Rows => rows;
    public IndexedCatalog? SelectedCatalog => selectedCatalog;
    public bool IsLoading => isLoading;
    public string Message => message;
    public string? ErrorCode => errorCode;
    public bool HasPublicationChanged => publicationChanged;
    public int PageNumber => pageNumber;
    public IndexedInventoryFilter AppliedFilter => appliedFilter;
    public string FilterSummary =>
        (appliedFilter.Text.Length == 0 ? "All saved entries" : $"{appliedFilter.Scope} contains “{appliedFilter.Text}”") +
        $" · Kind: {appliedFilter.Kind?.ToString() ?? "All"} · Bytes: {appliedFilter.MinBytes?.ToString() ?? "Any"} to {appliedFilter.MaxBytes?.ToString() ?? "Any"}" +
        $" · Filename suffix: {(appliedFilter.Extension.Length == 0 ? "Any" : appliedFilter.Extension)}" +
        $" · Modified UTC: {appliedFilter.ModifiedFrom?.ToString("yyyy-MM-dd") ?? "Any"} through {appliedFilter.ModifiedThrough?.ToString("yyyy-MM-dd") ?? "Any"}" +
        $" · Metadata: {appliedFilter.Metadata} · {(appliedFilter.CaseMode == IndexedInventoryCaseMode.MatchCase ? "Ordinal case-sensitive" : "Ordinal case-insensitive")} literal match · Source case capability: Unknown";
    public bool CanNextPage => !isLoading && nextCursor is not null;
    public bool CanPreviousPage => !isLoading && pageHistory.Count > 0;
    public bool CanNextCatalogPage => !isLoading && nextCatalogAfter is not null;
    public bool CanPreviousCatalogPage => !isLoading && catalogHistory.Count > 0;

    public Task RefreshAsync(CancellationToken cancellationToken = default)
        => RunAsync((request, token) => LoadCatalogsAsync(catalogAfter, 0, request, token), cancellationToken);

    public Task ApplyFilterAsync(IndexedInventoryFilter? filter, CancellationToken cancellationToken = default)
        => ApplyParsedAsync(() => IndexedInventoryFilter.Normalize(filter), cancellationToken);

    public Task ApplyDraftAsync(InventoryFilterDraft draft, CancellationToken cancellationToken = default)
        => ApplyParsedAsync(draft.Parse, cancellationToken);

    private Task ApplyParsedAsync(Func<IndexedInventoryFilter> parse, CancellationToken cancellationToken)
        => RunAsync(async (request, token) =>
        {
            // Parse only after admission, and before changing any applied state or paging.
            var normalized = parse();
            if (!Current(request)) return;
            var changed = appliedFilter != normalized;
            if (changed)
            {
                appliedFilter = normalized;
                SelectCatalog(selectedCatalog);
            }
            if (!Current(request)) return;
            if (selectedCatalog is null) await LoadCatalogsAsync(catalogAfter, 0, request, token);
            else await LoadPageAsync(changed ? null : pageAfter, !changed && pageNumber > 0 ? 2 : 0, request, token);
        }, cancellationToken);

    public Task ClearFilterAsync(CancellationToken cancellationToken = default)
        => ApplyFilterAsync(null, cancellationToken);

    public Task SelectRootAsync(string rootIdentity, CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        var catalog = catalogs.FirstOrDefault(item => item.RootIdentity == rootIdentity);
        if (catalog is null) return Task.CompletedTask;
        return RunAsync(async (request, token) =>
        {
            SelectCatalog(catalog);
            await LoadPageAsync(null, 0, request, token);
        }, cancellationToken);
    }

    public Task NextPageAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        var after = nextCursor;
        return after is null || isLoading ? Task.CompletedTask : RunAsync((request, token) => LoadPageAsync(after, 1, request, token), cancellationToken);
    }

    public Task PreviousPageAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        return pageHistory.Count == 0 || isLoading ? Task.CompletedTask :
            RunAsync((request, token) => LoadPageAsync(pageHistory[^1], -1, request, token), cancellationToken);
    }

    public Task NextCatalogPageAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        var after = nextCatalogAfter;
        return after is null || isLoading ? Task.CompletedTask : RunAsync((request, token) => LoadCatalogsAsync(after, 1, request, token), cancellationToken);
    }

    public Task PreviousCatalogPageAsync(CancellationToken cancellationToken = default)
    {
        EnsureUsable();
        return catalogHistory.Count == 0 || isLoading ? Task.CompletedTask :
            RunAsync((request, token) => LoadCatalogsAsync(catalogHistory[^1], -1, request, token), cancellationToken);
    }

    public void Cancel()
    {
        if (disposed) return;
        version++;
        active?.Cancel();
        isLoading = false;
        message = "Request cancelled. Any displayed rows remain saved indexed observations.";
        NotifyState();
    }

    private async Task LoadCatalogsAsync(string? after, int direction, long request, CancellationToken token)
    {
        if (!Current(request)) return;
        token.ThrowIfCancellationRequested();
        var result = await reader.ReadCatalogsAsync(new IndexedCatalogRequest(PageSize, after), token);
        token.ThrowIfCancellationRequested();
        if (!Current(request)) return;
        errorCode = null;
        if (result.Catalogs.Count > PageSize) throw new InventoryIndexException("inventory.ui_page_limit");
        var previousRoot = selectedCatalog?.RootIdentity;
        if (direction > 0) Push(catalogHistory, catalogAfter);
        else if (direction < 0) catalogHistory.RemoveAt(catalogHistory.Count - 1);
        catalogAfter = after;
        nextCatalogAfter = result.NextAfterRootIdentity;
        catalogs = Array.AsReadOnly(result.Catalogs.ToArray());
        var selected = catalogs.FirstOrDefault(item => item.RootIdentity == previousRoot) ?? catalogs.FirstOrDefault();
        SelectCatalog(selected);
        if (selected is null)
        {
            message = "No saved indexed catalogs are available on this page.";
            NotifyState();
            return;
        }
        await LoadPageAsync(null, 0, request, token);
    }

    private void SelectCatalog(IndexedCatalog? catalog)
    {
        selectedCatalog = catalog;
        rows = [];
        pageHistory.Clear();
        pageAfter = nextCursor = null;
        pageNumber = 0;
        publicationChanged = false;
        NotifyState();
    }

    private async Task LoadPageAsync(IndexedInventoryCursor? after, int direction, long request, CancellationToken token)
    {
        if (!Current(request)) return;
        token.ThrowIfCancellationRequested();
        var catalog = selectedCatalog;
        if (catalog is null) return;
        var generation = catalog.GenerationId;
        if (generation is null)
        {
            message = "This catalog has no published generation. Retained unpublished attempts are status only.";
            NotifyState();
            return;
        }
        var filter = appliedFilter;
        var result = await reader.ReadPageAsync(new IndexedInventoryRequest(catalog.RootIdentity, generation, PageSize, after, filter), token);
        token.ThrowIfCancellationRequested();
        if (!Current(request)) return;
        errorCode = null;
        if (result.Rows.Count > PageSize || result.Catalog.RootIdentity != catalog.RootIdentity || result.Catalog.GenerationId != generation ||
            (result.NextCursor is { } next && (next.RootIdentity != catalog.RootIdentity || next.GenerationId != generation || next.PageSize != PageSize ||
                IndexedInventoryFilter.Normalize(next.Filter) != filter)))
            throw new InventoryIndexException("inventory.ui_page_mismatch");
        if (direction == 1) { Push(pageHistory, pageAfter); pageNumber++; }
        else if (direction < 0) { pageHistory.RemoveAt(pageHistory.Count - 1); pageNumber--; }
        else if (direction == 0) { pageHistory.Clear(); pageNumber = 1; }
        pageAfter = after;
        nextCursor = result.NextCursor;
        selectedCatalog = result.Catalog;
        rows = Array.AsReadOnly(result.Rows.ToArray());
        publicationChanged = !result.IsCurrentPublication;
        var hasPredicate = filter.IsActive;
        message = rows.Count == 0 ? hasPredicate ? "No saved indexed results match the applied filters." : "This published catalog has no indexed entries." :
            $"Page {pageNumber} · {rows.Count} {(hasPredicate ? "matching " : string.Empty)}rows · Catalog total: {result.Catalog.EntryCount:N0} indexed entries · Path order";
        if (publicationChanged) message += " Published results changed. These rows remain pinned to the earlier generation; Refresh loads the current publication.";
        if (result.Catalog.HasRetainedUnpublishedAttempts) message += " Retained unpublished attempts exist; these rows are from the selected published generation.";
        NotifyState();
    }

    private Task RunAsync(Func<long, CancellationToken, Task> action, CancellationToken token)
    {
        EnsureUsable();
        // Reserve before invoking callbacks or the query port. Cancellation alone does not prove
        // an older SQLite call finished, so rapid Cancel/Refresh cannot queue unbounded work.
        if (Interlocked.Increment(ref inFlight) > 2)
        {
            Interlocked.Decrement(ref inFlight);
            errorCode = "inventory.request_limit";
            message = "An earlier inventory request is still finishing. Wait, then try Refresh again.";
            NotifyState();
            return Task.CompletedTask;
        }
        TaskCompletionSource? finished = null;
        CancellationTokenSource? cancellation = null;
        try
        {
            active?.Cancel();
            cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            active = cancellation;
            var request = ++version;
            finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pending) pending.Add(finished.Task);
            isLoading = true;
            errorCode = null;
            message = "Loading saved indexed results…";
            NotifyState();
            var task = ExecuteAsync(action, request, cancellation);
            return ObserveAsync(task, finished);
        }
        catch
        {
            if (finished is not null) { lock (pending) pending.Remove(finished.Task); }
            if (ReferenceEquals(active, cancellation)) active = null;
            cancellation?.Dispose();
            Interlocked.Decrement(ref inFlight);
            finished?.TrySetResult();
            throw;
        }
    }

    private async Task ObserveAsync(Task task, TaskCompletionSource finished)
    {
        try { await task; }
        finally
        {
            lock (pending) pending.Remove(finished.Task);
            Interlocked.Decrement(ref inFlight);
            finished.TrySetResult();
        }
    }

    private async Task ExecuteAsync(Func<long, CancellationToken, Task> action, long request, CancellationTokenSource cancellation)
    {
        try
        {
            if (!Current(request)) return;
            await action(request, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (Current(request)) message = "Request cancelled. Any displayed rows remain saved indexed observations.";
        }
        catch (Exception error)
        {
            if (Current(request))
            {
                var code = error is InventoryIndexException query ? query.Code : "inventory.read_failed";
                errorCode = code.Length <= 80 && code.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ? code : "inventory.read_failed";
                message = errorCode == "inventory.query_invalid_filter"
                    ? "Invalid filters. Use nonnegative whole bytes, minimum ≤ maximum, and UTC dates yyyy-MM-dd with From ≤ Through. Previous results are unchanged."
                    : $"Could not load saved indexed results ({errorCode}). Try Refresh.";
            }
        }
        finally
        {
            if (ReferenceEquals(active, cancellation)) active = null;
            cancellation.Dispose();
            if (Current(request)) { isLoading = false; NotifyState(); }
        }
    }

    private bool Current(long request) => !disposed && request == version;
    private void EnsureUsable() => ObjectDisposedException.ThrowIf(disposed, this);
    private static void Push<T>(List<T> history, T value)
    {
        if (history.Count >= HistoryLimit) history.RemoveAt(0);
        history.Add(value);
    }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(Catalogs), nameof(Rows), nameof(SelectedCatalog), nameof(IsLoading), nameof(Message), nameof(ErrorCode),
            nameof(HasPublicationChanged), nameof(PageNumber), nameof(AppliedFilter), nameof(FilterSummary), nameof(CanNextPage), nameof(CanPreviousPage), nameof(CanNextCatalogPage), nameof(CanPreviousCatalogPage) })
            OnPropertyChanged(name);
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new(propertyName));

    public ValueTask DisposeAsync()
    {
        if (disposal is not null) return new ValueTask(disposal);
        disposed = true;
        version++;
        active?.Cancel();
        Task[] tasks;
        lock (pending) tasks = pending.ToArray();
        isLoading = false;
        disposal = Task.WhenAll(tasks);
        return new ValueTask(disposal);
    }
}
