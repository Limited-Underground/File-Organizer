using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using FileOrganizer.Application;
using FileOrganizer.Persistence;

namespace FileOrganizer.Desktop;

internal sealed class MainWindow : Window
{
    private readonly IClassicDesktopStyleApplicationLifetime lifetime;
    private readonly LaunchOptions options;
    private readonly CancellationTokenSource closing = new();
    private readonly TextBlock status = new() { Text = "Starting local application services…", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock pageTitle = new() { Text = "Overview", FontSize = 26, FontWeight = FontWeight.SemiBold };
    private readonly TextBlock pageBody = new() { Text = "Your file-management workspace begins here.", TextWrapping = TextWrapping.Wrap };
    private DesktopRuntime? runtime;
    private AppDataPaths? paths;
    private StoreState? storeState;
    private Task startup = Task.CompletedTask;
    private bool shutdownStarted;
    private bool allowClose;
    private bool failed;

    public MainWindow(IClassicDesktopStyleApplicationLifetime lifetime, LaunchOptions options)
    {
        this.lifetime = lifetime;
        this.options = options;
        Title = "File Organizer by Limited Underground";
        Width = 1080; Height = 680; MinWidth = 760; MinHeight = 480;
        Background = new SolidColorBrush(Color.Parse("#111827"));
        Foreground = Brushes.WhiteSmoke;
        var navigation = new StackPanel { Spacing = 12, Margin = new Thickness(24) };
        navigation.Children.Add(new TextBlock { Text = "FILE ORGANIZER", FontWeight = FontWeight.Bold, FontSize = 16 });
        navigation.Children.Add(new TextBlock { Text = "by Limited Underground", Foreground = Brushes.LightGray, FontSize = 12, Margin = new Thickness(0, 0, 0, 28) });
        AddNavigation(navigation, "Overview", "Your file-management workspace begins here. This development shell initializes local storage and safely manages its background lifecycle.");
        AddNavigation(navigation, "Inventory", "Inventory indexing is a later V1 task. No folders are scanned by this shell.");
        AddNavigation(navigation, "Transfers", "The transfer engine is a later V1 task. This shell cannot copy, move or delete files.");
        AddNavigation(navigation, "Settings", "Application data is stored per user. Background services stop when this window closes. No network service or automatic startup is installed.");
        var sidebar = new Border { Background = new SolidColorBrush(Color.Parse("#1F2937")), Child = navigation };
        var main = new StackPanel { Margin = new Thickness(36), Spacing = 24 };
        main.Children.Add(new TextBlock { Text = "V1 DEVELOPMENT FOUNDATION", Foreground = Brushes.LightSkyBlue, FontSize = 12 });
        main.Children.Add(pageTitle);
        main.Children.Add(pageBody);
        main.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#1F2937")), Padding = new Thickness(20), CornerRadius = new CornerRadius(8), Child = status });
        main.Children.Add(new TextBlock { Text = "Local first · Apache 2.0 · No file operations in this checkpoint", Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("260,*") };
        layout.Children.Add(sidebar); Grid.SetColumn(main, 1); layout.Children.Add(main);
        Content = layout;
        Opened += (_, _) => startup = StartServicesAsync();
        Closing += OnClosing;
    }

    private void AddNavigation(StackPanel panel, string title, string body)
    {
        var button = new Button { Content = title, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(14, 10) };
        button.Click += (_, _) => { pageTitle.Text = title; pageBody.Text = body; };
        panel.Children.Add(button);
    }

    private async Task StartServicesAsync()
    {
        try
        {
            paths = options.DataRoot is null ? AppDataPaths.CreateDefault() : new AppDataPaths(options.DataRoot);
            runtime = new DesktopRuntime(paths);
            storeState = await runtime.StartAsync(closing.Token);
            status.Text = $"Ready · Database schema {storeState.SchemaVersion} · Successful startup {storeState.StartupCount}\nBackground host is running. Closing the window cancels and joins it.";
            if (options.SmokeTest && options.AutoClose)
            {
                await Task.Delay(800, closing.Token);
                using var image = new RenderTargetBitmap(new PixelSize((int)ClientSize.Width, (int)ClientSize.Height), new Vector(96, 96));
                image.Render(this);
                image.Save(Path.Combine(paths.RootDirectory, "shell.png"), PngBitmapEncoderOptions.Default);
                Close();
            }
        }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception exception)
        {
            failed = true;
            var code = exception is StoreInitializationException storeError ? storeError.ErrorCode : exception.GetType().Name;
            status.Text = $"Startup stopped safely ({code}). Retained application data was not reset. Close the window and inspect the local diagnostic event codes.";
            if (options.SmokeTest) Close();
        }
    }

    internal void ReportFatalError(Exception exception)
    {
        failed = true;
        status.Text = $"Application stopped safely ({exception.GetType().Name}).";
        Close();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        if (shutdownStarted) return;
        shutdownStarted = true;
        closing.Cancel();
        status.Text = "Stopping background services…";
        try
        {
            await startup;
            if (runtime is not null) await runtime.DisposeAsync();
            if (options.SmokeTest && paths is not null)
            {
                var result = new { success = !failed, schemaVersion = storeState?.SchemaVersion, startupCount = storeState?.StartupCount, workerCompleted = runtime?.Worker.Completion.IsCompletedSuccessfully ?? false, cleanShutdown = true };
                await File.WriteAllTextAsync(Path.Combine(paths.RootDirectory, "smoke-result.json"), JsonSerializer.Serialize(result));
            }
        }
        catch (Exception exception)
        {
            failed = true;
            Console.Error.WriteLine($"FO_SHUTDOWN_FAILURE ({exception.GetType().Name})");
        }
        finally
        {
            closing.Dispose();
            allowClose = true;
            lifetime.Shutdown(failed ? 1 : 0);
        }
    }
}
