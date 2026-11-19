using System.Text.Json;
using FileOrganizer.Application;
using Microsoft.Extensions.Logging;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class LoggingTests
{
    [Fact]
    public void LogRecordsExcludeMessagesScopesCategoriesAndExceptionContents()
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        const string sensitive = "PRIVATE-FIXTURE-CONTENT-C:\\Users\\fixture\\private.txt";
        using (var provider = new JsonLineLoggerProvider(paths))
        {
            var logger = provider.CreateLogger(sensitive);
            using var scope = logger.BeginScope(sensitive);
            logger.LogError(new EventId(4321, sensitive), new IOException(sensitive), "Sensitive {Value}", sensitive);
            Assert.Equal(0, provider.WriteFailures);
        }

        var text = File.ReadAllText(Path.Combine(paths.LogsDirectory, "application.jsonl"));
        Assert.DoesNotContain(sensitive, text);
        Assert.DoesNotContain("private.txt", text);
        using var record = JsonDocument.Parse(text);
        Assert.Equal(4321, record.RootElement.GetProperty("eventId").GetInt32());
        Assert.Equal("Error", record.RootElement.GetProperty("level").GetString());
        Assert.Equal(typeof(IOException).FullName, record.RootElement.GetProperty("exceptionType").GetString());
        Assert.False(record.RootElement.TryGetProperty("message", out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void RotationBoundsFileCountAndBytesWhileKeepingValidJson(int retainedFiles)
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        const long limit = 512;
        using (var provider = new JsonLineLoggerProvider(paths, new ApplicationOptions
        {
            LogFileSizeBytes = limit,
            RetainedLogFiles = retainedFiles,
        }))
        {
            var logger = provider.CreateLogger("rotation-fixture");
            for (var i = 0; i < 100; i++)
            {
                logger.LogInformation(new EventId(i), "fixture");
            }

            Assert.Equal(0, provider.WriteFailures);
        }

        var files = Directory.GetFiles(paths.LogsDirectory, "*.jsonl");
        Assert.Equal(retainedFiles, files.Length);
        foreach (var file in files)
        {
            Assert.InRange(new FileInfo(file).Length, 1L, limit);
            foreach (var line in File.ReadLines(file))
            {
                using var record = JsonDocument.Parse(line);
                Assert.True(record.RootElement.TryGetProperty("eventId", out _));
            }
        }

        var lastLine = File.ReadLines(Path.Combine(paths.LogsDirectory, "application.jsonl")).Last();
        using var latest = JsonDocument.Parse(lastLine);
        Assert.Equal(99, latest.RootElement.GetProperty("eventId").GetInt32());
    }
}
