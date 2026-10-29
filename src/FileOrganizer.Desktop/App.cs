using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.Styling;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

[assembly: InternalsVisibleTo("FileOrganizer.Tests")]

namespace FileOrganizer.Desktop;

public sealed class App : Avalonia.Application
{
    internal static LaunchOptions Options { get; set; } = new(false, null);

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(desktop, Options);
            desktop.MainWindow = window;
            Dispatcher.UIThread.UnhandledException += (_, args) =>
            {
                args.Handled = true;
                window.ReportFatalError(args.Exception);
            };
        }
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class DesktopRuntime : IAsyncDisposable
{
    private readonly ProfileLease lease;
    private readonly IHost host;
    private readonly TimeSpan shutdownDeadline;
    public BootstrapWorker Worker { get; }

    public DesktopRuntime(AppDataPaths paths, IApplicationStore? store = null, TimeSpan? shutdownTimeout = null)
    {
        shutdownDeadline = shutdownTimeout ?? TimeSpan.FromSeconds(10);
        lease = ProfileLease.Acquire(paths);
        try
        {
            var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
            var settingsFile = Path.Combine(AppContext.BaseDirectory, "runtime-settings.json");
            if (new FileInfo(settingsFile).Length > 64 * 1024) throw new InvalidOperationException("Runtime settings exceed the supported size.");
            var configuration = JsonSerializer.Deserialize<ApplicationOptions>(File.ReadAllText(settingsFile))
                ?? throw new InvalidOperationException("Runtime settings are invalid.");
            configuration.Validate();
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(new JsonLineLoggerProvider(paths.LogsDirectory, configuration));
            builder.Services.AddSingleton(configuration);
            builder.Services.AddSingleton(paths);
            if (store is null) builder.Services.AddSingleton<IApplicationStore, SqliteApplicationStore>();
            else builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<BootstrapWorker>();
            builder.Services.AddHostedService(services => services.GetRequiredService<BootstrapWorker>());
            builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(10));
            host = builder.Build();
            Worker = host.Services.GetRequiredService<BootstrapWorker>();
        }
        catch { lease.Dispose(); throw; }
    }

    public async Task<StoreState> StartAsync(CancellationToken cancellationToken)
    {
        await host.StartAsync(cancellationToken);
        return await Worker.Readiness.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(shutdownDeadline);
            await host.StopAsync(timeout.Token);
            if (Worker.ExecuteTask is not null) await Worker.Completion.WaitAsync(timeout.Token);
        }
        finally
        {
            if (Worker.ExecuteTask is null || Worker.Completion.IsCompleted)
            {
                host.Dispose();
                lease.Dispose();
            }
            else
            {
                // A timeout is not proof of worker termination. Retain exclusive ownership
                // until completion (or process termination), never admit a second writer.
                _ = Worker.Completion.ContinueWith(_ => { host.Dispose(); lease.Dispose(); },
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
