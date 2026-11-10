using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class InventoryProcessTests
{
    private static readonly TimeSpan ChildDeadline = TimeSpan.FromSeconds(15);

    [Theory]
    [InlineData("")]
    [InlineData("../outside")]
    [InlineData("C:/outside")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("NOT-A-SESSION")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    public void ChildSessionRejectsArbitraryPathsAndNonCanonicalIdentifiers(string supplied)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ProcessInventorySession.OpenChild(supplied));
        Assert.Equal("fixture.session.invalid_id", error.Message);
    }

    [Fact]
    public async Task CooperativeCancellationRetainsPriorPublishedGenerationAndHonestCancelledPrefix()
    {
        using var fixture = FixtureWorkspace.Create(evidenceTask: "FO-005-recovery-20261029");
        await new FixtureGenerator().GenerateAsync(fixture, new(104729,
            Enumerable.Range(0, 4).Select(index => new FixtureFileSpec($"file-{index}.bin", 9, $"key-{index}")).ToArray()));
        var source = new FixtureInventorySource(fixture);
        var snapshot = await source.CaptureAsync(CancellationToken.None);
        using var session = ProcessInventorySession.Create(snapshot);
        using var lease = ProfileLease.Acquire(session.Paths);
        await new SqliteApplicationStore(session.Paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(session.Paths);
        var prior = await store.IndexAsync(snapshot with { Entries = snapshot.Entries.Take(1).ToArray() }, 1, CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        await AssertSourceStillBound(source, session);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.IndexAsync(snapshot, 2, cancellation.Token,
            new CancelAfterDurableBatch(cancellation)));
        var state = await new SqliteInventoryStore(session.Paths).ReadStateAsync(CancellationToken.None);
        Assert.Equal(prior.GenerationId, Assert.Single(state.Roots).GenerationId);
        Assert.Equal(1, Assert.Single(state.Roots).EntryCount);
        var cancelled = Assert.Single(state.Runs, run => run.Status == InventoryRunStatus.Cancelled);
        Assert.Equal(session.Fingerprint, cancelled.Fingerprint);
        Assert.Equal(2, cancelled.CommittedEntries);
        Assert.Equal(2L, CountPrefix(session.Paths, cancelled.GenerationId));
        Assert.Equal("ok", Scalar(session.Paths, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task KilledWriterLeavesRunningDurablePrefixAndFreshProcessResumesSameGeneration()
    {
        using var fixture = FixtureWorkspace.Create(evidenceTask: "FO-005-recovery-20261029");
        await new FixtureGenerator().GenerateAsync(fixture, new(104729,
            Enumerable.Range(0, 6).Select(index => new FixtureFileSpec($"file-{index}.bin", 19 + index, $"key-{index}")).ToArray()));
        var source = new FixtureInventorySource(fixture);
        var snapshot = await source.CaptureAsync(CancellationToken.None);
        using var session = ProcessInventorySession.Create(snapshot);
        InventoryIndexResult prior;
        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            await new SqliteApplicationStore(session.Paths).InitializeAsync(CancellationToken.None);
            prior = await new SqliteInventoryStore(session.Paths).IndexAsync(
                snapshot with { Entries = snapshot.Entries.Take(1).ToArray() }, 1, CancellationToken.None);
        }

        await AssertSourceStillBound(source, session);
        InventoryIndexResult paused;
        int killedProcessId;
        int killedExitCode;
        using (var child = StartChild(session, "pause"))
        {
            var stderr = ReadBoundedText(child.StandardError);
            try
            {
                var handshake = await ReadBoundedLine(child.StandardOutput).WaitAsync(ChildDeadline);
                paused = JsonSerializer.Deserialize<InventoryIndexResult>(handshake)!;
                Assert.NotNull(paused);
                Assert.Equal(InventoryRunStatus.Running, paused.Status);
                Assert.Equal(session.Fingerprint, paused.Fingerprint);
                Assert.Equal(2, paused.CommittedEntries);
                Assert.False(paused.Published);
                Assert.False(child.HasExited);
                killedProcessId = child.Id;
                session.WriteReceipt("pause-observation.json", JsonSerializer.SerializeToUtf8Bytes(new { killedProcessId, paused }));
                child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync().WaitAsync(ChildDeadline);
                killedExitCode = child.ExitCode;
                Assert.NotEqual(0, killedExitCode);
                Assert.Equal(string.Empty, await stderr.WaitAsync(ChildDeadline));
            }
            finally { await KillOwnedChildIfAlive(child); }
        }

        session.ValidateProfileBoundary();
        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            var state = await new SqliteInventoryStore(session.Paths).ReadStateAsync(CancellationToken.None);
            var running = Assert.Single(state.Runs, run => run.GenerationId == paused.GenerationId);
            Assert.Equal(InventoryRunStatus.Running, running.Status); // No graceful cancellation ran.
            Assert.Equal(2, running.CommittedEntries);
            Assert.Equal(prior.GenerationId, Assert.Single(state.Roots).GenerationId);
            Assert.Equal(2L, CountPrefix(session.Paths, paused.GenerationId));
            Assert.Equal("ok", Scalar(session.Paths, "PRAGMA integrity_check;"));
            session.WriteReceipt("killed-observation.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                killedProcessId, killedExitCode, running, previousPublishedGeneration = prior.GenerationId,
                integrity = "ok", prefixEntries = 2,
            }));
        }

        await AssertSourceStillBound(source, session);
        using (var child = StartChild(session, "resume"))
        {
            var stdout = ReadBoundedText(child.StandardOutput);
            var stderr = ReadBoundedText(child.StandardError);
            try
            {
                await child.WaitForExitAsync().WaitAsync(ChildDeadline);
                Assert.Equal(0, child.ExitCode);
                Assert.Equal(string.Empty, await stderr.WaitAsync(ChildDeadline));
                var resumed = JsonSerializer.Deserialize<InventoryIndexResult>(await stdout.WaitAsync(ChildDeadline))!;
                Assert.NotNull(resumed);
                Assert.Equal(paused.GenerationId, resumed.GenerationId);
                Assert.Equal(InventoryRunStatus.Completed, resumed.Status);
                Assert.Equal(snapshot.Entries.Count, resumed.CommittedEntries);
                Assert.True(resumed.Published);
                session.WriteReceipt("resume-observation.json", JsonSerializer.SerializeToUtf8Bytes(new { resumed, exitCode = child.ExitCode }));
            }
            finally { await KillOwnedChildIfAlive(child); }
        }

        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            var state = await new SqliteInventoryStore(session.Paths).ReadStateAsync(CancellationToken.None);
            var root = Assert.Single(state.Roots);
            Assert.Equal(paused.GenerationId, root.GenerationId);
            Assert.Equal(6, root.EntryCount);
            Assert.Equal(6, root.ObjectCount);
            Assert.Equal(2, state.Runs.Count);
            Assert.Equal("ok", Scalar(session.Paths, "PRAGMA integrity_check;"));
        }
    }

    private static async Task AssertSourceStillBound(FixtureInventorySource source, ProcessInventorySession session)
    {
        using var timeout = new CancellationTokenSource(ChildDeadline);
        var current = await source.CaptureAsync(timeout.Token);
        Assert.Equal(session.Fingerprint, PreparedInventorySnapshot.Create(current).Fingerprint);
    }

    internal static Process StartChild(ProcessInventorySession session, string mode, string? fixtureId = null, string? generationId = null)
    {
        session.ValidateProfileBoundary();
        var dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", "dotnet.exe"));
        if (!File.Exists(dotnet)) throw new InvalidOperationException("fixture.child.runtime_missing");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = ProcessInventorySession.FindCheckout(),
        };
        start.ArgumentList.Add(typeof(InventoryProcessTests).Assembly.Location);
        start.ArgumentList.Add(fixtureId is null ? "--fixture-index-child" : "--fixture-capture-child");
        start.ArgumentList.Add(session.SessionId);
        if (fixtureId is not null) start.ArgumentList.Add(fixtureId);
        start.ArgumentList.Add(mode);
        if (fixtureId is not null) start.ArgumentList.Add(generationId ?? "new");
        return Process.Start(start) ?? throw new InvalidOperationException("fixture.child.start_failed");
    }

    internal static async Task<string> ReadBoundedLine(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory()) != 0)
        {
            if (buffer[0] == '\n') return text.ToString();
            if (text.Length >= 4096) throw new InvalidOperationException("fixture.child.output_limit");
            text.Append(buffer[0]);
        }
        throw new InvalidOperationException("fixture.child.no_handshake");
    }

    internal static async Task<string> ReadBoundedText(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[512];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) != 0)
        {
            if (text.Length + read > 8192) throw new InvalidOperationException("fixture.child.output_limit");
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    internal static async Task KillOwnedChildIfAlive(Process child)
    {
        if (child.HasExited) return;
        child.Kill(entireProcessTree: false);
        await child.WaitForExitAsync().WaitAsync(ChildDeadline);
    }

    private static long CountPrefix(AppDataPaths paths, string generation)
    {
        using var connection = Open(paths);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM InventoryDirectoryEntry WHERE GenerationId=$generation AND Ordinal<2;";
        command.Parameters.AddWithValue("$generation", generation);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static object? Scalar(AppDataPaths paths, string sql)
    {
        using var connection = Open(paths);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static SqliteConnection Open(AppDataPaths paths)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class CancelAfterDurableBatch(CancellationTokenSource cancellation) : IProgress<InventoryIndexResult>
    {
        public void Report(InventoryIndexResult value)
        {
            if (value.CommittedEntries == 2 && value.Status == InventoryRunStatus.Running) cancellation.Cancel();
        }
    }
}
