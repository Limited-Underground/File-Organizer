using System.Diagnostics;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Xunit;

namespace FileOrganizer.Tests;

/// <summary>Actual owned-process termination and fresh-process replay, not hardware power-loss testing.</summary>
public sealed class OperationProcessRecoveryTests
{
    private const string EvidenceTask = "FO-007-operation-journal-20261118";
    private static readonly TimeSpan ParentDeadline = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(OperationBoundary.CreatePartial)]
    [InlineData(OperationBoundary.WriteAndFlush)]
    [InlineData(OperationBoundary.Promote)]
    [InlineData(OperationBoundary.RetainSource)]
    public async Task KilledAfterFileEffectBeforeResultFreshProcessRecoversSafely(OperationBoundary boundary)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native restart identity requires Windows.");
        // The existing nonce/session transport fixes the checkout, task and profile scope.
        // The root identity transports only an operation ID, never a source path.
        var id = Guid.NewGuid();
        using var session = ProcessInventorySession.Create(new InventorySnapshot(id.ToString("N"), []), EvidenceTask);
        var fixture = OperationFileFixture.CreateSession(session);
        var plan = fixture.Plan(OperationMode.Move);
        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            await new SqliteApplicationStore(session.Paths).InitializeAsync(CancellationToken.None);
            await new SqliteOperationJournal(session.Paths).AppendAsync(id,
                new OperationJournalEntry(0, OperationRecordKind.Plan, Plan: plan), CancellationToken.None);
        }

        int killedPid;
        int killedExit;
        var mode = boundary switch
        {
            OperationBoundary.CreatePartial => "create",
            OperationBoundary.WriteAndFlush => "write",
            OperationBoundary.Promote => "promote",
            _ => "retain"
        };
        using (var child = StartChild(session, mode))
        {
            killedPid = child.Id;
            var stderr = InventoryProcessTests.ReadBoundedText(child.StandardError);
            try
            {
                var line = await InventoryProcessTests.ReadBoundedLine(child.StandardOutput).WaitAsync(ParentDeadline);
                Assert.Equal("operation.paused:" + boundary, line.TrimEnd('\r'));
                session.WriteReceipt("pause-observation.json", JsonSerializer.SerializeToUtf8Bytes(new
                    { Boundary = boundary.ToString(), ProcessId = child.Id, OperationId = id }));
                // Only the Process object created above is terminated; never a name/PID search.
                child.Kill(entireProcessTree: false);
                await child.WaitForExitAsync().WaitAsync(ParentDeadline);
                killedExit = child.ExitCode;
                Assert.NotEqual(0, killedExit);
                Assert.Equal(string.Empty, await stderr.WaitAsync(ParentDeadline));
            }
            finally { await InventoryProcessTests.KillOwnedChildIfAlive(child); }
        }

        OperationSnapshot interrupted;
        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            session.ValidateProfileBoundary();
            interrupted = OperationFileFixture.OpenSession(session).Observe();
            var records = await new SqliteOperationJournal(session.Paths).ReadAsync(id, CancellationToken.None);
            Assert.Equal(OperationRecordKind.Intent, records[^1].Kind);
            Assert.Equal(boundary, records[^1].Boundary);
            Assert.Equal(plan.ExpectedSource, interrupted.Source ?? interrupted.RetainedSource);
            if (boundary is OperationBoundary.CreatePartial or OperationBoundary.WriteAndFlush)
            {
                Assert.Null(interrupted.Destination);
                Assert.NotNull(interrupted.Partial);
                if (boundary == OperationBoundary.CreatePartial)
                {
                    Assert.Equal(0, interrupted.Partial.Length);
                    Assert.Equal("E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855", interrupted.Partial.Sha256);
                }
                else
                {
                    Assert.Equal(plan.ExpectedSource.Length, interrupted.Partial.Length);
                    Assert.Equal(plan.ExpectedSource.Sha256, interrupted.Partial.Sha256);
                }
            }
            else
            {
                Assert.Null(interrupted.Partial);
                Assert.NotNull(interrupted.Destination);
                Assert.Equal(plan.ExpectedSource.Length, interrupted.Destination.Length);
                Assert.Equal(plan.ExpectedSource.Sha256, interrupted.Destination.Sha256);
            }
            if (boundary == OperationBoundary.RetainSource) Assert.Null(interrupted.Source);
            else Assert.Null(interrupted.RetainedSource);
        }
        session.WriteReceipt("killed-observation.json", JsonSerializer.SerializeToUtf8Bytes(new
            { Boundary = boundary.ToString(), ProcessId = killedPid, ExitCode = killedExit, State = interrupted }));

        using (var child = StartChild(session, "resume"))
        {
            var stdout = InventoryProcessTests.ReadBoundedText(child.StandardOutput);
            var stderr = InventoryProcessTests.ReadBoundedText(child.StandardError);
            try
            {
                await child.WaitForExitAsync().WaitAsync(ParentDeadline);
                Assert.Equal(string.Empty, await stderr.WaitAsync(ParentDeadline));
                Assert.Equal(0, child.ExitCode);
                Assert.Equal(boundary == OperationBoundary.CreatePartial ? "operation.needs_attention" : "operation.completed",
                    (await stdout.WaitAsync(ParentDeadline)).Trim());
            }
            finally { await InventoryProcessTests.KillOwnedChildIfAlive(child); }
        }

        using (var lease = ProfileLease.Acquire(session.Paths))
        {
            var restored = OperationFileFixture.OpenSession(session).Observe();
            var records = await new SqliteOperationJournal(session.Paths).ReadAsync(id, CancellationToken.None);
            if (boundary == OperationBoundary.CreatePartial)
            {
                Assert.Equal(interrupted, restored);
                Assert.Equal(plan.ExpectedSource, restored.Source);
                Assert.Null(restored.RetainedSource);
                Assert.Equal(3, records.Count);
                Assert.Equal(OperationRecordKind.Blocked, records[^1].Kind);
                Assert.Equal(OperationBoundary.CreatePartial, records[^1].Boundary);
            }
            else
            {
                Assert.Null(restored.Source);
                Assert.Null(restored.Partial);
                Assert.Equal(plan.ExpectedSource, restored.RetainedSource);
                // The complete partial survives restart and is promoted with its same native identity.
                Assert.Equal(boundary == OperationBoundary.WriteAndFlush ? interrupted.Partial : interrupted.Destination,
                    restored.Destination);
                Assert.Equal(9, records.Count);
                Assert.Equal(OperationRecordKind.Result, records[^1].Kind);
                Assert.Equal(OperationBoundary.RetainSource, records[^1].Boundary);
            }
            Assert.Equal(Enumerable.Range(0, records.Count), records.Select(record => record.Sequence));
            Assert.Equal(restored, records[^1].Observation);
            session.WriteReceipt("resume-observation.json", JsonSerializer.SerializeToUtf8Bytes(new
                { Boundary = boundary.ToString(), State = restored, JournalCount = records.Count, NativeIdentityVerified = true }));
        }
    }

    private static Process StartChild(ProcessInventorySession session, string mode)
    {
        session.ValidateProfileBoundary();
        var dotnet = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "..", "..", "..", "dotnet.exe"));
        if (!File.Exists(dotnet)) throw new InvalidOperationException("fixture.child.runtime_missing");
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            CreateNoWindow = true, WorkingDirectory = ProcessInventorySession.FindCheckout()
        };
        start.ArgumentList.Add(typeof(OperationProcessRecoveryTests).Assembly.Location);
        start.ArgumentList.Add("--fixture-operation-child");
        start.ArgumentList.Add(session.SessionId);
        start.ArgumentList.Add(mode);
        return Process.Start(start) ?? throw new InvalidOperationException("fixture.child.start_failed");
    }

    internal static async Task<int> RunChildAsync(string sessionId, string mode)
    {
        if (mode is not ("create" or "write" or "promote" or "retain" or "resume")) return 64;
        try
        {
            if (!OperatingSystem.IsWindows()) return 64;
            using var session = ProcessInventorySession.OpenChild(sessionId, EvidenceTask);
            using var lease = ProfileLease.Acquire(session.Paths);
            session.ValidateProfileBoundary();
            if (!Guid.TryParseExact(session.Snapshot.RootIdentity, "N", out var id) || id == Guid.Empty) return 64;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var journal = new SqliteOperationJournal(session.Paths);
            var records = await journal.ReadAsync(id, deadline.Token);
            if (records.Count == 0 || records[0].Kind != OperationRecordKind.Plan || records[0].Plan is not { } plan) return 64;
            var fixture = OperationFileFixture.OpenSession(session);
            var pauseAt = mode switch
            {
                "create" => OperationBoundary.CreatePartial,
                "write" => OperationBoundary.WriteAndFlush,
                "promote" => OperationBoundary.Promote,
                _ => OperationBoundary.RetainSource
            };
            fixture.Fault = (boundary, after) =>
            {
                if (mode == "resume" || !after || boundary != pauseAt) return;
                // The file effect has returned; its journal Result is still uncommitted.
                Console.Out.WriteLine("operation.paused:" + boundary);
                Console.Out.Flush();
                deadline.Token.WaitHandle.WaitOne();
                deadline.Token.ThrowIfCancellationRequested();
            };
            var result = await new OperationRunner(journal, fixture).RunAsync(id, plan, deadline.Token);
            var pending = records[^1].Boundary;
            var blocked = mode == "resume" && pending == OperationBoundary.CreatePartial;
            if (result != (blocked ? OperationOutcome.NeedsAttention : OperationOutcome.Completed)) return 65;
            if (mode == "resume")
            {
                // The ambiguous empty partial remains untouched. A completed write is re-flushed
                // exactly once; completed rename effects are certified, never re-applied.
                if (fixture.ApplyCount(OperationBoundary.CreatePartial) != 0 ||
                    fixture.ApplyCount(OperationBoundary.WriteAndFlush) != (pending == OperationBoundary.WriteAndFlush ? 1 : 0) ||
                    fixture.ApplyCount(OperationBoundary.Promote) != (pending == OperationBoundary.WriteAndFlush ? 1 : 0) ||
                    fixture.ApplyCount(OperationBoundary.RetainSource) !=
                        (pending is OperationBoundary.WriteAndFlush or OperationBoundary.Promote ? 1 : 0)) return 65;
            }
            session.WriteReceipt("resume-result.json", JsonSerializer.SerializeToUtf8Bytes(new
            {
                Outcome = result.ToString(), State = fixture.Observe(),
                CreateEffects = fixture.ApplyCount(OperationBoundary.CreatePartial),
                WriteEffects = fixture.ApplyCount(OperationBoundary.WriteAndFlush),
                PromotionEffects = fixture.ApplyCount(OperationBoundary.Promote),
                RetentionEffects = fixture.ApplyCount(OperationBoundary.RetainSource)
            }));
            Console.Out.WriteLine(blocked ? "operation.needs_attention" : "operation.completed");
            Console.Out.Flush();
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("fixture.operation.failed:" + error.GetType().Name);
            Console.Error.Flush();
            return 66;
        }
    }
}
