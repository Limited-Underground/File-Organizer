namespace FileOrganizer.Application;

public enum OperationMode { Copy, Move }
public enum OperationBoundary { CreatePartial, WriteAndFlush, Promote, RetainSource }
public enum OperationRecordKind { Plan, Intent, Result, Blocked }
public enum OperationOutcome { Completed, NeedsAttention }

public sealed record OperationFile(string Identity, long Length, string Sha256);
public sealed record OperationSnapshot(OperationFile? Source, OperationFile? Partial,
    OperationFile? Destination, OperationFile? RetainedSource);
public sealed record OperationPlan(string SourceKey, string DestinationKey, OperationMode Mode, OperationFile ExpectedSource);
public sealed record OperationJournalEntry(int Sequence, OperationRecordKind Kind,
    OperationBoundary? Boundary = null, OperationPlan? Plan = null, OperationSnapshot? Observation = null);

/// <summary>Append returns only after the record is durable. An ambiguous failure requires a fresh read.</summary>
public interface IOperationJournal
{
    Task<IReadOnlyList<OperationJournalEntry>> ReadAsync(Guid id, CancellationToken token);
    Task AppendAsync(Guid id, OperationJournalEntry entry, CancellationToken token);
}

/// <summary>
/// Keys resolve only inside the adapter's admitted scope. Observations use stable object identities
/// plus content evidence. Null means verified absence. Inaccessible or uncertain observations throw.
/// Apply must revalidate against observedBefore, use exclusive/no-overwrite operations, and retain
/// the source when asked to retire it. It throws OperationConflictException on a stale precondition
/// without touching the changed entry. Never infer object identity from name, size or digest alone.
/// </summary>
public interface IOperationFileSystem
{
    Task<OperationSnapshot> ObserveAsync(Guid id, OperationPlan plan, CancellationToken token);

    /// <summary>WriteAndFlush on an already complete matching partial performs a verified re-flush only.
    /// Promotion and source retention must preserve identity and return only after the adapter's
    /// required durability operations. Recovery observations must be sufficient to certify a rename.</summary>
    Task<OperationSnapshot> ApplyAsync(Guid id, OperationPlan plan, OperationBoundary boundary,
        OperationSnapshot observedBefore, CancellationToken token);
}

/// <summary>The adapter detected a changed precondition before making the requested change.</summary>
public sealed class OperationConflictException() : IOException("The operation precondition changed.");

/// <summary>
/// Executes a single add-only item. The caller holds the profile lease across each run, including
/// restart recovery. The runner is separate from production filesystem and queue implementations.
/// </summary>
public sealed class OperationRunner(IOperationJournal journal, IOperationFileSystem files)
{
    private const string EmptyHash = "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855";
    private static readonly SemaphoreSlim Coordinator = new(1, 1);

    public async Task<OperationOutcome> RunAsync(Guid id, OperationPlan plan, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (id == Guid.Empty || !ValidKey(plan.SourceKey) || !ValidKey(plan.DestinationKey) ||
            string.Equals(plan.SourceKey, plan.DestinationKey, StringComparison.OrdinalIgnoreCase) ||
            !Enum.IsDefined(plan.Mode) || !ValidFile(plan.ExpectedSource))
            throw new ArgumentException("Invalid operation plan.", nameof(plan));

        await Coordinator.WaitAsync(token).ConfigureAwait(false);
        try { return await RunExclusiveAsync(id, plan, token).ConfigureAwait(false); }
        finally { Coordinator.Release(); }
    }

    private async Task<OperationOutcome> RunExclusiveAsync(Guid id, OperationPlan plan, CancellationToken token)
    {
        var records = await journal.ReadAsync(id, token).ConfigureAwait(false);
        var boundaryCount = plan.Mode == OperationMode.Copy ? 3 : 4;
        if (records.Count > 1 + (boundaryCount * 2))
            throw InvalidJournal();

        var sequence = 0;
        OperationJournalEntry? Next() => sequence < records.Count ? records[sequence++] : null;
        async Task AppendAsync(OperationRecordKind kind, OperationBoundary? boundary = null,
            OperationSnapshot? observation = null)
        {
            await journal.AppendAsync(id, new(sequence, kind, boundary,
                kind == OperationRecordKind.Plan ? plan : null, observation), token).ConfigureAwait(false);
            sequence++;
        }
        async Task<OperationOutcome> BlockAsync(OperationBoundary boundary, OperationSnapshot observed)
        {
            await AppendAsync(OperationRecordKind.Blocked, boundary, observed).ConfigureAwait(false);
            return OperationOutcome.NeedsAttention;
        }

        var savedPlan = Next();
        if (savedPlan is null)
            await AppendAsync(OperationRecordKind.Plan).ConfigureAwait(false);
        else if (savedPlan != new OperationJournalEntry(0, OperationRecordKind.Plan, Plan: plan))
            throw new InvalidOperationException("Operation ID already belongs to a different or invalid plan.");

        var expected = new OperationSnapshot(plan.ExpectedSource, null, null, null);
        for (var index = 0; index < boundaryCount; index++)
        {
            var boundary = (OperationBoundary)index;
            var intent = Next();
            if (intent is not null)
            {
                if (IsBlocked(intent, sequence - 1, boundary, sequence == records.Count))
                    return OperationOutcome.NeedsAttention;
                if (intent != new OperationJournalEntry(sequence - 1, OperationRecordKind.Intent, boundary, Observation: expected))
                    throw InvalidJournal();
            }
            else
            {
                var current = await ObserveAsync(id, plan, token).ConfigureAwait(false);
                if (current != expected)
                    return await BlockAsync(boundary, current).ConfigureAwait(false);
                await AppendAsync(OperationRecordKind.Intent, boundary, expected).ConfigureAwait(false);
            }

            var result = Next();
            if (result is not null)
            {
                if (IsBlocked(result, sequence - 1, boundary, sequence == records.Count))
                    return OperationOutcome.NeedsAttention;
                if (result.Sequence != sequence - 1 || result.Kind != OperationRecordKind.Result ||
                    result.Boundary != boundary || result.Plan is not null ||
                    !ValidResult(boundary, expected, result.Observation))
                    throw InvalidJournal();

                expected = result.Observation!;
                continue;
            }

            var observed = await ObserveAsync(id, plan, token).ConfigureAwait(false);
            var knownAfter = boundary != OperationBoundary.CreatePartial && ValidResult(boundary, expected, observed);
            if (knownAfter && boundary is OperationBoundary.Promote or OperationBoundary.RetainSource)
            {
                await AppendAsync(OperationRecordKind.Result, boundary, observed).ConfigureAwait(false);
                expected = observed;
                continue;
            }

            // A partial created between intent and result has no durable identity proof. Preserve it
            // for review. A fully written owned partial is re-flushed before recording its result.
            if (observed != expected && !(knownAfter && boundary == OperationBoundary.WriteAndFlush))
                return await BlockAsync(boundary, observed).ConfigureAwait(false);

            token.ThrowIfCancellationRequested();
            OperationSnapshot applied;
            try
            {
                applied = await files.ApplyAsync(id, plan, boundary, observed, token).ConfigureAwait(false);
            }
            catch (OperationConflictException)
            {
                var changed = await ObserveAsync(id, plan, token).ConfigureAwait(false);
                return await BlockAsync(boundary, changed).ConfigureAwait(false);
            }
            var verified = await ObserveAsync(id, plan, token).ConfigureAwait(false);
            if (applied != verified || !ValidResult(boundary, expected, verified))
                return await BlockAsync(boundary, verified).ConfigureAwait(false);

            await AppendAsync(OperationRecordKind.Result, boundary, verified).ConfigureAwait(false);
            expected = verified;
        }
        if (sequence < records.Count) throw InvalidJournal();
        return OperationOutcome.Completed;
    }

    private async Task<OperationSnapshot> ObserveAsync(Guid id, OperationPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var observed = await files.ObserveAsync(id, plan, token).ConfigureAwait(false);
        if (!ValidObservation(observed))
            throw new IOException("Operation file observation is invalid.");
        return observed;
    }

    private static bool ValidResult(OperationBoundary boundary, OperationSnapshot before, OperationSnapshot? after)
    {
        if (!ValidObservation(after)) return false;
        return boundary switch
        {
            OperationBoundary.CreatePartial => after!.Partial is { Length: 0, Sha256: EmptyHash } created &&
                created.Identity != before.Source!.Identity && after == before with { Partial = created },
            OperationBoundary.WriteAndFlush => before.Partial is { } partial && after == before with
            {
                Partial = partial with { Length = before.Source!.Length, Sha256 = before.Source.Sha256 }
            },
            OperationBoundary.Promote => after == before with { Destination = before.Partial, Partial = null },
            OperationBoundary.RetainSource => after == before with { RetainedSource = before.Source, Source = null },
            _ => false
        };
    }

    private static bool IsBlocked(OperationJournalEntry entry, int sequence, OperationBoundary boundary, bool isLast)
    {
        if (entry.Kind != OperationRecordKind.Blocked) return false;
        if (entry.Sequence != sequence || entry.Boundary != boundary || entry.Plan is not null ||
            !ValidObservation(entry.Observation) || !isLast)
            throw InvalidJournal();
        return true;
    }

    private static bool ValidKey(string? value) => value is { Length: > 0 and <= 1024 } &&
        !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
    private static bool ValidFile(OperationFile? file) => file is { Length: >= 0, Sha256.Length: 64 } &&
        ValidKey(file.Identity) && file.Sha256.All(char.IsAsciiHexDigitUpper);
    private static bool ValidObservation(OperationSnapshot? snapshot) => snapshot is not null &&
        (snapshot.Source is null || ValidFile(snapshot.Source)) &&
        (snapshot.Partial is null || ValidFile(snapshot.Partial)) &&
        (snapshot.Destination is null || ValidFile(snapshot.Destination)) &&
        (snapshot.RetainedSource is null || ValidFile(snapshot.RetainedSource));

    private static InvalidOperationException InvalidJournal() => new("Operation journal sequence or evidence is invalid.");
}
