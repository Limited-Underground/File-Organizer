using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace FileOrganizer.Application;

/// <summary>
/// Records only event metadata. Messages, state, scopes, category, exception text and stack traces
/// are intentionally excluded because those fields can contain file paths or file content.
/// </summary>
public sealed class JsonLineLoggerProvider : ILoggerProvider
{
    private readonly object gate = new();
    private readonly string directory;
    private readonly ApplicationOptions options;
    private bool disposed;
    private long writeFailures;

    public JsonLineLoggerProvider(AppDataPaths paths, ApplicationOptions? options = null)
        : this(paths?.LogsDirectory ?? throw new ArgumentNullException(nameof(paths)), options)
    {
    }

    public JsonLineLoggerProvider(string logsDirectory, ApplicationOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(logsDirectory);
        this.options = options ?? new ApplicationOptions();
        this.options.Validate();
        directory = Path.GetFullPath(logsDirectory);
        Directory.CreateDirectory(directory);
    }

    public long WriteFailures => Interlocked.Read(ref writeFailures);

    public ILogger CreateLogger(string categoryName) => new PrivacyLogger(this);

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
        }
    }

    private string GetPath(int index) => Path.Combine(directory, index == 0 ? "application.jsonl" : $"application.{index}.jsonl");

    private void Write(LogLevel level, EventId eventId, Exception? exception)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            timestamp = DateTimeOffset.UtcNow,
            level = level.ToString(),
            eventId = eventId.Id,
            exceptionType = exception?.GetType().FullName,
            exceptionCode = exception?.HResult,
        }) + "\n");

        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            try
            {
                // Keep the configured total bound even for unusual dynamically generated exception types.
                if (bytes.LongLength > options.LogFileSizeBytes)
                {
                    Interlocked.Increment(ref writeFailures);
                    return;
                }

                var current = GetPath(0);
                if (File.Exists(current) && new FileInfo(current).Length + bytes.LongLength > options.LogFileSizeBytes)
                {
                    var oldest = GetPath(options.RetainedLogFiles - 1);
                    if (File.Exists(oldest))
                    {
                        File.Delete(oldest);
                    }

                    for (var index = options.RetainedLogFiles - 2; index >= 0; index--)
                    {
                        var source = GetPath(index);
                        if (File.Exists(source))
                        {
                            File.Move(source, GetPath(index + 1));
                        }
                    }
                }

                using var stream = new FileStream(current, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(bytes);
            }
            catch (IOException)
            {
                // A diagnostics failure must not crash startup or hide the store's actual result.
                Interlocked.Increment(ref writeFailures);
            }
            catch (UnauthorizedAccessException)
            {
                Interlocked.Increment(ref writeFailures);
            }
        }
    }

    private sealed class PrivacyLogger(JsonLineLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, eventId, exception);
            }
        }
    }
}
