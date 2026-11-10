using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileOrganizer.Tests.Fixtures;

/// <summary>Generates only new test-owned data; this is not an inventory or transfer engine.</summary>
internal sealed class FixtureGenerator(Action<FixtureCheckpoint, int>? onCheckpoint = null)
{
    private const int ChunkBytes = 4096;
    public static DateTime ModifiedUtc { get; } = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<FixtureRunResult> GenerateAsync(FixtureWorkspace workspace, FixturePlan plan,
        FixtureFault? fault = null, CancellationToken cancellationToken = default)
    {
        workspace.BeginGeneration();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        FaultSession? session = null;
        var created = 0;
        var completed = 0;
        string? manifestHash = null;
        try
        {
            session = new FaultSession(workspace, cancellation, fault, onCheckpoint);
            var prepared = Prepare(workspace, plan, cancellation.Token);
            var manifest = new FixtureManifest(1, "sha256-blocks-v1", plan.Seed, prepared.Directories, prepared.Files);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            if (bytes.Length > 1024 * 1024) throw new FixtureSafetyException("quota.manifest");
            manifestHash = Convert.ToHexString(SHA256.HashData(bytes));
            session.Reach(FixtureCheckpoint.BeforeManifest);
            workspace.WriteControl("manifest.json", bytes);
            session.Reach(FixtureCheckpoint.AfterManifest);
            foreach (var directory in prepared.Directories)
            {
                session.Reach(FixtureCheckpoint.BeforeDirectoryCreate);
                workspace.EnsureDirectory(directory);
                session.Reach(FixtureCheckpoint.AfterDirectoryCreate);
            }
            foreach (var entry in prepared.Files)
            {
                session.Reach(FixtureCheckpoint.BeforeFileCreate);
                var writer = workspace.CreatePayload(entry.RelativePath, entry.Length);
                created++;
                session.Reach(FixtureCheckpoint.AfterFileCreate);
                for (long offset = 0; offset < entry.Length;)
                {
                    workspace.CheckBudget(cancellation.Token);
                    var chunk = ContentChunk(plan.Seed, entry.ContentKey, offset, (int)Math.Min(ChunkBytes, entry.Length - offset));
                    await writer.WriteAsync(chunk, cancellation.Token);
                    offset += chunk.Length;
                    session.Reach(FixtureCheckpoint.AfterChunkWrite);
                }
                var actual = await writer.CompleteAsync(ModifiedUtc, cancellation.Token);
                if (!StringComparer.Ordinal.Equals(entry.Sha256, actual)) throw new FixtureSafetyException("fixture.content_mismatch");
                completed++;
                session.Reach(FixtureCheckpoint.AfterFileComplete);
            }
            session.Reach(FixtureCheckpoint.BeforeReceipt);
            session.RequireTriggeredFault();
            var receipt = MakeReceipt("Completed", null);
            workspace.WriteControl("receipt.json", JsonSerializer.SerializeToUtf8Bytes(receipt));
            return new FixtureRunResult(bytes, receipt);
        }
        catch (Exception exception)
        {
            var code = exception switch
            {
                OperationCanceledException => "fixture.cancelled",
                FixtureSafetyException safety => safety.Code,
                FixtureInjectedException => "fixture.injected_fault",
                _ => "fixture.failed"
            };
            var outcome = exception is OperationCanceledException ? "Cancelled" : "Failed";
            try { workspace.WriteControl("receipt.json", JsonSerializer.SerializeToUtf8Bytes(MakeReceipt(outcome, code))); }
            catch (Exception receiptFailure) { throw new AggregateException("fixture.failure_receipt_unavailable", exception, receiptFailure); }
            throw;
        }

        FixtureReceipt MakeReceipt(string outcome, string? error) => new(workspace.RunId, outcome, created, completed,
            workspace.WrittenBytes, manifestHash, session?.LastPoint?.ToString(), session?.LastOccurrence ?? 0, error);
    }

    private static (string[] Directories, FixtureManifestEntry[] Files) Prepare(FixtureWorkspace workspace, FixturePlan plan, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(plan.Files);
        if (plan.Files.Count > workspace.Limits.MaxFiles || (plan.Directories?.Count ?? 0) > workspace.Limits.MaxDirectories)
            throw new FixtureSafetyException("quota.plan_count");
        var source = new FixtureFileSpec[plan.Files.Count];
        for (var index = 0; index < source.Length; index++) source[index] = plan.Files[index];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in source)
        {
            workspace.CheckBudget(token);
            if (entry is null || string.IsNullOrEmpty(entry.ContentKey) || entry.ContentKey.Length > 128 || entry.ContentKey.Any(char.IsControl))
                throw new FixtureSafetyException("plan.invalid_content_key");
            _ = StrictUtf8.GetBytes(entry.ContentKey);
            _ = workspace.ResolveOwnedPath(entry.RelativePath);
            if (!paths.Add(entry.RelativePath)) throw new FixtureSafetyException("plan.path_collision");
            if (entry.Length < 0 || entry.Length > workspace.Limits.MaxFileBytes || entry.Length > workspace.Limits.MaxTotalBytes - total)
                throw new FixtureSafetyException("quota.plan_bytes");
            total = checked(total + entry.Length);
            AddParents(entry.RelativePath);
        }
        var directoryCount = plan.Directories?.Count ?? 0;
        for (var index = 0; index < directoryCount; index++)
        {
            workspace.CheckBudget(token);
            var directory = plan.Directories![index];
            AddDirectory(directory);
            AddParents(directory);
        }
        if (directories.Keys.Any(paths.Contains)) throw new FixtureSafetyException("plan.file_directory_collision");
        var files = new List<FixtureManifestEntry>(source.Length);
        foreach (var entry in source.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal))
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < entry.Length;)
            {
                workspace.CheckBudget(token);
                var chunk = ContentChunk(plan.Seed, entry.ContentKey, offset, (int)Math.Min(ChunkBytes, entry.Length - offset));
                digest.AppendData(chunk);
                offset += chunk.Length;
            }
            files.Add(new(entry.RelativePath, entry.Length, entry.ContentKey, Convert.ToHexString(digest.GetHashAndReset()), ModifiedUtc));
        }
        return (directories.Values.Order(StringComparer.Ordinal).ToArray(), files.ToArray());

        void AddDirectory(string path)
        {
            _ = workspace.ResolveOwnedPath(path);
            if (directories.TryGetValue(path, out var existing))
            {
                if (!StringComparer.Ordinal.Equals(existing, path)) throw new FixtureSafetyException("plan.directory_case_collision");
                return;
            }
            if (directories.Count >= workspace.Limits.MaxDirectories) throw new FixtureSafetyException("quota.plan_directories");
            directories.Add(path, path);
        }
        void AddParents(string path)
        {
            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1)) AddDirectory(path[..slash]);
        }
    }

    private static byte[] ContentChunk(int seed, string key, long offset, int length)
    {
        var result = new byte[length];
        for (var written = 0; written < length;)
        {
            var blockIndex = (offset + written) / 32;
            var inBlock = (int)((offset + written) % 32);
            var recipe = "file-organizer.fixture.v1\0" + seed.ToString(CultureInfo.InvariantCulture) + "\0" + key + "\0" + blockIndex.ToString(CultureInfo.InvariantCulture);
            var block = SHA256.HashData(StrictUtf8.GetBytes(recipe));
            var count = Math.Min(32 - inBlock, length - written);
            block.AsSpan(inBlock, count).CopyTo(result.AsSpan(written));
            written += count;
        }
        return result;
    }

    private sealed class FaultSession
    {
        private readonly FixtureWorkspace workspace;
        private readonly CancellationTokenSource cancellation;
        private readonly FixtureFault? fault;
        private readonly Action<FixtureCheckpoint, int>? observer;
        private readonly Dictionary<FixtureCheckpoint, int> counts = [];
        private bool triggered;
        public FixtureCheckpoint? LastPoint { get; private set; }
        public int LastOccurrence { get; private set; }

        public FaultSession(FixtureWorkspace workspace, CancellationTokenSource cancellation, FixtureFault? fault, Action<FixtureCheckpoint, int>? observer)
        {
            if (fault is not null && (!Enum.IsDefined(fault.Point) || !Enum.IsDefined(fault.Action) || fault.Occurrence < 1))
                throw new FixtureSafetyException("fault.invalid_trigger");
            this.workspace = workspace; this.cancellation = cancellation; this.fault = fault; this.observer = observer;
        }
        public void Reach(FixtureCheckpoint point)
        {
            var occurrence = counts.GetValueOrDefault(point) + 1;
            counts[point] = occurrence;
            LastPoint = point; LastOccurrence = occurrence;
            observer?.Invoke(point, occurrence);
            if (fault is not null && fault.Point == point && fault.Occurrence == occurrence)
            {
                triggered = true;
                if (fault.Action == FixtureFaultAction.Throw) throw new FixtureInjectedException(point, occurrence);
                cancellation.Cancel();
            }
            // Recheck ownership after the observation/fault hook, immediately before the next action.
            workspace.CheckBudget(cancellation.Token);
        }

        public void RequireTriggeredFault()
        {
            if (fault is not null && !triggered) throw new FixtureSafetyException("fault.not_reached");
        }
    }
}
