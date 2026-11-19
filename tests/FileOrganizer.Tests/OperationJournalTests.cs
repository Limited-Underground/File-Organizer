using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class OperationJournalTests
{
    public enum Fault { Interruption, FullDisk, Locked }

    public static IEnumerable<object[]> JournalCases()
    {
        foreach (var sequence in Enumerable.Range(0, 9))
        foreach (var after in new[] { false, true })
        foreach (var fault in Enum.GetValues<Fault>())
            yield return [sequence, after, fault];
    }

    public static IEnumerable<object[]> FileCases()
    {
        foreach (var step in Enum.GetValues<OperationBoundary>())
        foreach (var after in new[] { false, true })
        foreach (var fault in Enum.GetValues<Fault>())
            yield return [step, after, fault];
    }

    public static IEnumerable<object[]> ChangedFileCases()
    {
        foreach (var step in Enum.GetValues<OperationBoundary>())
        foreach (var after in new[] { false, true })
            yield return [step, after];
    }

    [Theory]
    [MemberData(nameof(JournalCases))]
    public async Task EveryJournalCommitCanBeInterruptedAndReopened(int sequence, bool after, Fault fault)
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var plan = fixture.Plan(OperationMode.Move);
        var id = Guid.NewGuid();
        var triggered = false;
        void Inject(OperationJournalEntry entry, bool committed)
        {
            if (!triggered && entry.Sequence == sequence && committed == after)
            {
                triggered = true;
                throw Error(fault);
            }
        }

        var journal = new SqliteOperationJournal(session.Paths, entry => Inject(entry, false));
        var runner = new OperationRunner(new AfterCommitJournal(journal, entry => Inject(entry, true)), fixture);
        await Assert.ThrowsAnyAsync<IOException>(() => runner.RunAsync(id, plan));
        Assert.True(triggered);
        var recovered = await new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan);
        var expected = sequence == 2 && !after ? OperationOutcome.NeedsAttention : OperationOutcome.Completed;
        Assert.Equal(expected, recovered);
        AssertSafe(fixture, plan, recovered);
        await AssertTranscript(session.Paths, id, expected);
    }

    [Theory]
    [MemberData(nameof(FileCases))]
    public async Task EveryFileEffectCanBeInterruptedAndReopened(OperationBoundary boundary, bool after, Fault fault)
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var plan = fixture.Plan(OperationMode.Move);
        var id = Guid.NewGuid();
        var triggered = false;
        fixture.Fault = (step, committed) =>
        {
            if (!triggered && step == boundary && committed == after)
            {
                triggered = true;
                throw Error(fault);
            }
        };
        await Assert.ThrowsAnyAsync<IOException>(() => new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan));
        Assert.True(triggered);
        fixture.Fault = null;
        var recovered = await new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan);
        var expected = boundary == OperationBoundary.CreatePartial && after
            ? OperationOutcome.NeedsAttention : OperationOutcome.Completed;
        Assert.Equal(expected, recovered);
        AssertSafe(fixture, plan, recovered);
        await AssertTranscript(session.Paths, id, expected);
        if (after && boundary is OperationBoundary.Promote or OperationBoundary.RetainSource)
            Assert.Equal(1, fixture.ApplyCount(boundary));
    }

    [Theory]
    [MemberData(nameof(ChangedFileCases))]
    public async Task ChangedDestinationAtAnyFileBoundaryIsPreserved(OperationBoundary boundary, bool after)
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var plan = fixture.Plan(OperationMode.Move);
        var id = Guid.NewGuid();
        OperationFile? foreign = null;
        fixture.Fault = (step, committed) =>
        {
            if (step != boundary || committed != after) return;
            fixture.ChangeDestination();
            foreign = fixture.Observe().Destination;
            throw new IOException("fixture destination changed");
        };
        await Assert.ThrowsAnyAsync<IOException>(() => new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan));
        Assert.NotNull(foreign);
        fixture.Fault = null;
        Assert.Equal(OperationOutcome.NeedsAttention,
            await new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan));
        Assert.Equal(foreign, fixture.Observe().Destination);
        Assert.Equal(plan.ExpectedSource, fixture.Observe().Source ?? fixture.Observe().RetainedSource);
    }

    [Theory]
    [InlineData(OperationBoundary.CreatePartial)]
    [InlineData(OperationBoundary.WriteAndFlush)]
    [InlineData(OperationBoundary.Promote)]
    [InlineData(OperationBoundary.RetainSource)]
    public async Task ChangedSourceBeforeAnyEffectIsRetained(OperationBoundary boundary)
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var plan = fixture.Plan(OperationMode.Move);
        var id = Guid.NewGuid();
        OperationFile? changed = null;
        fixture.Fault = (step, after) =>
        {
            if (step != boundary || after) return;
            fixture.ChangeSource();
            changed = fixture.Observe().Source;
        };
        var result = await new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan);
        Assert.Equal(OperationOutcome.NeedsAttention, result);
        Assert.NotNull(changed);
        Assert.Equal(changed, fixture.Observe().Source);
        Assert.Null(fixture.Observe().RetainedSource);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(6, false)]
    [InlineData(6, true)]
    [InlineData(7, false)]
    [InlineData(7, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public async Task ChangedDestinationAtAnyJournalBoundaryIsPreserved(int sequence, bool after)
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var plan = fixture.Plan(OperationMode.Move);
        var id = Guid.NewGuid();
        OperationFile? foreign = null;
        void Inject(OperationJournalEntry entry, bool committed)
        {
            if (entry.Sequence != sequence || committed != after) return;
            fixture.ChangeDestination();
            foreign = fixture.Observe().Destination;
            throw new IOException("fixture destination changed");
        }

        var journal = new SqliteOperationJournal(session.Paths, entry => Inject(entry, false));
        var runner = new OperationRunner(new AfterCommitJournal(journal, entry => Inject(entry, true)), fixture);
        await Assert.ThrowsAnyAsync<IOException>(() => runner.RunAsync(id, plan));
        Assert.NotNull(foreign);
        var result = await new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan);
        // A committed terminal result reports the earlier outcome and performs no new mutation.
        Assert.Equal(sequence == 8 && after ? OperationOutcome.Completed : OperationOutcome.NeedsAttention, result);
        Assert.Equal(foreign, fixture.Observe().Destination);
        Assert.Equal(plan.ExpectedSource, fixture.Observe().Source ?? fixture.Observe().RetainedSource);
    }

    [Theory]
    [InlineData(false, Fault.Interruption)]
    [InlineData(false, Fault.FullDisk)]
    [InlineData(false, Fault.Locked)]
    [InlineData(true, Fault.Interruption)]
    [InlineData(true, Fault.FullDisk)]
    [InlineData(true, Fault.Locked)]
    public async Task IncompleteWriteIsPreservedAndCompleteUnflushedWriteIsReflushed(bool complete, Fault fault)
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var plan = fixture.Plan(OperationMode.Move);
        var id = Guid.NewGuid();
        var reached = false;
        fixture.BeforeFlush = allBytes =>
        {
            if (allBytes != complete) return;
            reached = true;
            throw Error(fault);
        };
        await Assert.ThrowsAnyAsync<IOException>(() => new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan));
        Assert.True(reached);
        var interrupted = fixture.Observe();
        Assert.NotNull(interrupted.Partial);
        Assert.Null(interrupted.Destination);
        Assert.Equal(plan.ExpectedSource, interrupted.Source);
        fixture.BeforeFlush = null;

        var recovered = await new OperationRunner(new SqliteOperationJournal(session.Paths), fixture).RunAsync(id, plan);
        Assert.Equal(complete ? OperationOutcome.Completed : OperationOutcome.NeedsAttention, recovered);
        AssertSafe(fixture, plan, recovered);
        if (!complete) Assert.Equal(interrupted.Partial, fixture.Observe().Partial);
        else Assert.Equal(1, fixture.ApplyCount(OperationBoundary.WriteAndFlush));
    }

    [Theory]
    [InlineData(false, Fault.Interruption)]
    [InlineData(false, Fault.FullDisk)]
    [InlineData(false, Fault.Locked)]
    [InlineData(true, Fault.Interruption)]
    [InlineData(true, Fault.FullDisk)]
    [InlineData(true, Fault.Locked)]
    public async Task BlockedRecordCanBeInterruptedAndReopened(bool after, Fault fault)
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var plan = fixture.Plan(OperationMode.Move);
        var id = Guid.NewGuid();
        fixture.ChangeDestination();
        var original = fixture.Observe();
        Assert.NotNull(original.Destination);

        var fired = false;
        void Inject(OperationJournalEntry entry, bool committed)
        {
            if (entry.Kind != OperationRecordKind.Blocked || committed != after) return;
            fired = true;
            throw Error(fault);
        }

        var journal = new SqliteOperationJournal(session.Paths, entry => Inject(entry, false));
        var runner = new OperationRunner(new AfterCommitJournal(journal, entry => Inject(entry, true)), fixture);
        await Assert.ThrowsAnyAsync<IOException>(() => runner.RunAsync(id, plan));
        Assert.True(fired);

        var reopened = new SqliteOperationJournal(session.Paths);
        Assert.Equal(OperationOutcome.NeedsAttention, await new OperationRunner(reopened, fixture).RunAsync(id, plan));
        Assert.Equal(original, fixture.Observe());
        var records = await reopened.ReadAsync(id, CancellationToken.None);
        Assert.Equal(2, records.Count);
        Assert.Equal(new(1, OperationRecordKind.Blocked, OperationBoundary.CreatePartial, Observation: original), records[1]);
    }


    [Fact]
    public async Task CopyIsAddOnlyAndReplayCannotReuseIdForAnotherPlan()
    {
        using var session = await Session.StartAsync();
        var fixture = session.Fixture;
        var journal = new SqliteOperationJournal(session.Paths);
        var runner = new OperationRunner(journal, fixture);
        var plan = fixture.Plan(OperationMode.Copy);
        var id = Guid.NewGuid();
        Assert.Equal(OperationOutcome.Completed, await runner.RunAsync(id, plan));
        Assert.Equal(OperationOutcome.Completed, await runner.RunAsync(id, plan));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(id, plan with { DestinationKey = "other" }));
        AssertSafe(fixture, plan, OperationOutcome.Completed);
        Assert.Equal(1, fixture.ApplyCount(OperationBoundary.Promote));
        Assert.Equal(0, fixture.ApplyCount(OperationBoundary.RetainSource));
    }

    [Fact]
    public async Task JournalRefusesStaleSequenceAndRetainsOriginalPlan()
    {
        using var session = await Session.StartAsync();
        var journal = new SqliteOperationJournal(session.Paths);
        var id = Guid.NewGuid();
        var entry = new OperationJournalEntry(0, OperationRecordKind.Plan, Plan: session.Fixture.Plan(OperationMode.Copy));
        await journal.AppendAsync(id, entry, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AppendAsync(id, entry, CancellationToken.None));
        Assert.Equal(entry, Assert.Single(await new SqliteOperationJournal(session.Paths).ReadAsync(id, CancellationToken.None)));
    }

    private static async Task AssertTranscript(AppDataPaths paths, Guid id, OperationOutcome expected)
    {
        var records = await new SqliteOperationJournal(paths).ReadAsync(id, CancellationToken.None);
        Assert.Equal(Enumerable.Range(0, records.Count), records.Select(entry => entry.Sequence));
        Assert.Equal(OperationRecordKind.Plan, records[0].Kind);
        Assert.Equal(expected == OperationOutcome.Completed ? OperationRecordKind.Result : OperationRecordKind.Blocked,
            records[^1].Kind);
    }

    private static void AssertSafe(OperationFileFixture fixture, OperationPlan plan, OperationOutcome outcome)
    {
        var state = fixture.Observe();
        Assert.Equal(plan.ExpectedSource, state.Source ?? state.RetainedSource);
        if (outcome == OperationOutcome.Completed)
        {
            Assert.Null(state.Partial);
            Assert.NotNull(state.Destination);
            Assert.Equal(plan.ExpectedSource.Sha256, state.Destination.Sha256);
            Assert.Equal(plan.ExpectedSource.Length, state.Destination.Length);
            if (plan.Mode == OperationMode.Move) Assert.Null(state.Source);
            else Assert.Null(state.RetainedSource);
        }
        else Assert.Null(state.RetainedSource);
    }

    private static IOException Error(Fault fault) => new InjectedIoException(fault switch
    {
        Fault.FullDisk => unchecked((int)0x80070070),
        Fault.Locked => unchecked((int)0x80070020),
        _ => unchecked((int)0x80004005)
    });

    private sealed class InjectedIoException : IOException
    {
        public InjectedIoException(int code) : base("injected fixture fault", code) { }
    }

    private sealed class AfterCommitJournal(IOperationJournal journal, Action<OperationJournalEntry> after) : IOperationJournal
    {
        public Task<IReadOnlyList<OperationJournalEntry>> ReadAsync(Guid id, CancellationToken token) => journal.ReadAsync(id, token);
        public async Task AppendAsync(Guid id, OperationJournalEntry entry, CancellationToken token)
        {
            await journal.AppendAsync(id, entry, token);
            after(entry);
        }
    }

    private sealed class Session(OperationFileFixture fixture, AppDataPaths paths, ProfileLease lease) : IDisposable
    {
        public OperationFileFixture Fixture { get; } = fixture;
        public AppDataPaths Paths { get; } = paths;

        public static async Task<Session> StartAsync()
        {
            var fixture = new OperationFileFixture();
            var paths = new AppDataPaths(Path.Combine(fixture.Root, "profile"));
            var lease = ProfileLease.Acquire(paths);
            try
            {
                await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
                return new Session(fixture, paths, lease);
            }
            catch { lease.Dispose(); throw; }
        }

        public void Dispose() => lease.Dispose();
    }
}
