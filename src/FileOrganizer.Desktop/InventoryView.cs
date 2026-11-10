using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input;
using FileOrganizer.Application;

namespace FileOrganizer.Desktop;

internal sealed class InventoryView : Grid, IDisposable
{
    private readonly InventoryViewModel model;
    private readonly ComboBox catalogs = new() { HorizontalAlignment = HorizontalAlignment.Stretch, MinWidth = 200 };
    private readonly ListBox rows = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock generation = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.LightGray };
    private readonly Button refresh = new() { Content = "Refresh" };
    private readonly Button cancel = new() { Content = "Cancel" };
    private readonly Button previous = new() { Content = "Previous page" };
    private readonly Button next = new() { Content = "Next page" };
    private readonly Button previousCatalogs = new() { Content = "Previous catalogs" };
    private readonly Button nextCatalogs = new() { Content = "More catalogs" };
    private readonly TextBox search = new() { PlaceholderText = "Search saved names or paths", MaxLength = 128, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox scope = new() { ItemsSource = new[] { "Name", "Path" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox kind = new() { ItemsSource = new[] { "All kinds", "Files", "Folders", "Excluded links" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button apply = new() { Content = "Apply" };
    private readonly Button clear = new() { Content = "Clear" };
    private readonly TextBlock appliedSummary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 11, Foreground = Brushes.LightGray };
    private readonly TextBox minBytes = new() { PlaceholderText = "Any", MaxLength = 32 };
    private readonly TextBox maxBytes = new() { PlaceholderText = "Any", MaxLength = 32 };
    private readonly TextBox modifiedFrom = new() { PlaceholderText = "yyyy-MM-dd", MaxLength = 20 };
    private readonly TextBox modifiedThrough = new() { PlaceholderText = "yyyy-MM-dd", MaxLength = 20 };
    private readonly ComboBox caseMode = new() { ItemsSource = new[] { "Ignore case", "Match case" }, SelectedIndex = 0 };
    private readonly ComboBox metadata = new() { ItemsSource = new[] { "Any", "Available", "Unavailable" }, SelectedIndex = 0 };
    private bool rendering;
    private bool disposed;

    public InventoryView(InventoryViewModel model)
    {
        this.model = model;
        RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto,*,Auto");
        RowSpacing = 10;
        var tools = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 8 };
        catalogs.ItemTemplate = new FuncDataTemplate<IndexedCatalog>((item, _) => new TextBlock
        {
            Text = item is null ? string.Empty : $"{item.RootIdentity} · {item.EntryCount:N0} entries{(item.HasPublishedGeneration ? string.Empty : " · Unpublished")}",
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        tools.Children.Add(catalogs); tools.Children.Add(refresh); Grid.SetColumn(refresh, 1);
        tools.Children.Add(cancel); Grid.SetColumn(cancel, 2);
        Children.Add(tools);
        var filters = new Grid { ColumnDefinitions = new ColumnDefinitions("*,90,125,Auto,Auto"), ColumnSpacing = 8 };
        ToolTip.SetTip(search, "Literal substring using the selected ordinal letter case policy. Whitespace is preserved. No wildcards or regular expressions.");
        ToolTip.SetTip(scope, "Search the saved entry name or its root-relative path.");
        ToolTip.SetTip(kind, "Restrict saved results by entry kind.");
        filters.Children.Add(search);
        filters.Children.Add(scope); Grid.SetColumn(scope, 1);
        filters.Children.Add(kind); Grid.SetColumn(kind, 2);
        filters.Children.Add(apply); Grid.SetColumn(apply, 3);
        filters.Children.Add(clear); Grid.SetColumn(clear, 4);
        Children.Add(filters); Grid.SetRow(filters, 1);
        var state = new StackPanel { Spacing = 5 };
        state.Children.Add(new TextBlock
        {
            Text = "Saved indexed observations · Drive availability not checked · Not an atomic snapshot · Source case capability: Unknown",
            TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.LightSkyBlue,
        });
        state.Children.Add(generation);
        state.Children.Add(appliedSummary);
        var advanced = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,1.1*,1.1*,1.3*,1.4*"), RowDefinitions = new RowDefinitions("Auto"), ColumnSpacing = 8 };
        AddEditor(advanced, "Min bytes", minBytes, 0, 0, "Minimum file bytes");
        AddEditor(advanced, "Max bytes", maxBytes, 1, 0, "Maximum file bytes");
        AddEditor(advanced, "From UTC", modifiedFrom, 2, 0, "Modified from (UTC day)");
        AddEditor(advanced, "Through UTC", modifiedThrough, 3, 0, "Modified through (UTC day)");
        AddEditor(advanced, "Case", caseMode, 4, 0, "Letter case policy");
        AddEditor(advanced, "Metadata", metadata, 5, 0, "Metadata availability");
        ToolTip.SetTip(caseMode, "Explicit ordinal comparison; source case capability remains unknown.");
        var expander = new Expander { Header = "Advanced filters · size, UTC date, case and metadata", Content = advanced, HorizontalAlignment = HorizontalAlignment.Stretch };
        Children.Add(expander); Grid.SetRow(expander, 2);
        Children.Add(state); Grid.SetRow(state, 3);
        Children.Add(message); Grid.SetRow(message, 4);
        var headers = CreateRow("Path", "Size", "Modified (UTC)", "Status", true);
        Children.Add(headers); Grid.SetRow(headers, 5);
        rows.ItemTemplate = new FuncDataTemplate<IndexedInventoryRow>((item, _) => item is null ? new TextBlock() :
            CreateRow(item.RelativePath,
                item.Kind == InventoryEntryKind.Directory ? "—" : !item.HasSize ? "Unknown" : item.Length.ToString("N0", CultureInfo.CurrentCulture) + " B",
                item.HasUsableMetadata ? item.ModifiedUtc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : "Unknown",
                item.ErrorCode is not null ? "Unreadable (" + item.ErrorCode + ")" : item.Kind switch
                {
                    InventoryEntryKind.Directory => "Indexed folder",
                    InventoryEntryKind.ExcludedReparse => "Link excluded",
                    _ => "Indexed file",
                }, false));
        Children.Add(rows); Grid.SetRow(rows, 6);
        var paging = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto,Auto"), ColumnSpacing = 8 };
        paging.Children.Add(previousCatalogs); paging.Children.Add(nextCatalogs); Grid.SetColumn(nextCatalogs, 1);
        paging.Children.Add(previous); Grid.SetColumn(previous, 3); paging.Children.Add(next); Grid.SetColumn(next, 4);
        Children.Add(paging); Grid.SetRow(paging, 7);
        apply.Click += async (_, _) => { if (!disposed) await ApplyDraftAsync(); };
        clear.Click += async (_, _) =>
        {
            if (disposed) return;
            search.Text = string.Empty;
            scope.SelectedIndex = kind.SelectedIndex = caseMode.SelectedIndex = metadata.SelectedIndex = 0;
            minBytes.Text = maxBytes.Text = modifiedFrom.Text = modifiedThrough.Text = string.Empty;
            await model.ClearFilterAsync();
        };
        foreach (var editor in new[] { search, minBytes, maxBytes, modifiedFrom, modifiedThrough }) editor.KeyDown += async (_, args) =>
        {
            if (!disposed && args.Key == Key.Enter && apply.IsEnabled)
            {
                args.Handled = true;
                await ApplyDraftAsync();
            }
        };
        refresh.Click += async (_, _) => { if (!disposed) await model.RefreshAsync(); };
        cancel.Click += (_, _) => { if (!disposed) model.Cancel(); };
        previous.Click += async (_, _) => { if (!disposed) await model.PreviousPageAsync(); };
        next.Click += async (_, _) => { if (!disposed) await model.NextPageAsync(); };
        previousCatalogs.Click += async (_, _) => { if (!disposed) await model.PreviousCatalogPageAsync(); };
        nextCatalogs.Click += async (_, _) => { if (!disposed) await model.NextCatalogPageAsync(); };
        catalogs.SelectionChanged += async (_, _) =>
        {
            if (!disposed && !rendering && catalogs.SelectedItem is IndexedCatalog catalog)
                await model.SelectRootAsync(catalog.RootIdentity);
        };
        model.PropertyChanged += ModelChanged;
        RenderState();
    }

    private Task ApplyDraftAsync()
    {
        var selectedKind = kind.SelectedIndex switch
        {
            1 => (InventoryEntryKind?)InventoryEntryKind.File,
            2 => InventoryEntryKind.Directory,
            3 => InventoryEntryKind.ExcludedReparse,
            _ => null,
        };
        return model.ApplyDraftAsync(new InventoryFilterDraft(search.Text ?? string.Empty,
            scope.SelectedIndex == 1 ? IndexedInventorySearchScope.Path : IndexedInventorySearchScope.Name, selectedKind,
            minBytes.Text ?? "", maxBytes.Text ?? "", modifiedFrom.Text ?? "", modifiedThrough.Text ?? "",
            caseMode.SelectedIndex == 1 ? IndexedInventoryCaseMode.MatchCase : IndexedInventoryCaseMode.IgnoreCase,
            metadata.SelectedIndex switch { 1 => IndexedInventoryMetadataMode.Available, 2 => IndexedInventoryMetadataMode.Unavailable, _ => IndexedInventoryMetadataMode.Any }));
    }

    private static void AddEditor(Grid grid, string label, Control editor, int column, int row, string? automationName = null)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 11 });
        Avalonia.Automation.AutomationProperties.SetName(editor, automationName ?? label);
        ToolTip.SetTip(editor, automationName ?? label);
        panel.Children.Add(editor);
        grid.Children.Add(panel); Grid.SetColumn(panel, column); Grid.SetRow(panel, row);
    }

    private static Grid CreateRow(string path, string size, string modified, string status, bool header)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,105,155,145"), ColumnSpacing = 8, Margin = new Thickness(4, 6) };
        var values = new[] { path, size, modified, status };
        for (var index = 0; index < values.Length; index++)
        {
            var cell = new TextBlock { Text = values[index], TextTrimming = TextTrimming.CharacterEllipsis, FontSize = header ? 12 : 11,
                FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center };
            ToolTip.SetTip(cell, values[index]);
            row.Children.Add(cell); Grid.SetColumn(cell, index);
        }
        return row;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess()) RenderState();
        else Dispatcher.UIThread.Post(RenderState);
    }

    private void RenderState()
    {
        if (disposed) return;
        rendering = true;
        try
        {
            if (!ReferenceEquals(catalogs.ItemsSource, model.Catalogs)) catalogs.ItemsSource = model.Catalogs;
            catalogs.SelectedItem = model.SelectedCatalog is null ? null : model.Catalogs.FirstOrDefault(item => item.RootIdentity == model.SelectedCatalog.RootIdentity);
            if (!ReferenceEquals(rows.ItemsSource, model.Rows)) rows.ItemsSource = model.Rows;
            message.Text = model.Message;
            message.Foreground = model.ErrorCode is not null ? Brushes.LightSalmon : model.HasPublicationChanged ? Brushes.Khaki : Brushes.WhiteSmoke;
            generation.Text = model.SelectedCatalog?.GenerationId is { } id ? "Saved generation: " + id : "No published generation selected";
            appliedSummary.Text = "Applied: " + model.FilterSummary;
            ToolTip.SetTip(appliedSummary, appliedSummary.Text);
            // Draft controls are intentionally not assigned here: typing is not an applied query,
            // and late responses must never overwrite an editor's pending text or kind choice.
            search.IsEnabled = scope.IsEnabled = kind.IsEnabled = apply.IsEnabled = clear.IsEnabled = !model.IsLoading;
            minBytes.IsEnabled = maxBytes.IsEnabled = modifiedFrom.IsEnabled = modifiedThrough.IsEnabled = caseMode.IsEnabled = metadata.IsEnabled = !model.IsLoading;
            refresh.IsEnabled = !model.IsLoading;
            cancel.IsEnabled = model.IsLoading;
            catalogs.IsEnabled = !model.IsLoading;
            previous.IsEnabled = model.CanPreviousPage;
            next.IsEnabled = model.CanNextPage;
            previousCatalogs.IsEnabled = model.CanPreviousCatalogPage;
            nextCatalogs.IsEnabled = model.CanNextCatalogPage;
        }
        finally { rendering = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        model.PropertyChanged -= ModelChanged;
    }
}
