using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using FileOrganizer.Application;

namespace FileOrganizer.Tests.Fixtures;

internal sealed record StreamingFixtureOptions
{
    public int FileCount { get; init; } = 100;
    public int DirectoryCount { get; init; } = 10;
    public long TotalBytes { get; init; } = 3200;
    public int Seed { get; init; } = 104729;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(45);
    public long MinimumFreeBytes { get; init; } = 256L * 1024 * 1024;
}

internal sealed record StreamingFixtureOwnership(int FormatVersion, string Id, string RootIdentity);
internal sealed record StreamingOracleEntry(InventoryEntry Observation, string Sha256);
internal sealed record StreamingFixtureVerification(int FileCount, int DirectoryCount, long TotalBytes, string OracleSha256, long ElapsedMilliseconds);

/// <summary>Generates physical disposable files without retaining payload handles or an in-memory oracle.</summary>
internal sealed class StreamingFixture : IDisposable
{
    internal const string EvidenceTask = "FO-005-streaming-20261029";
    private readonly WindowsDirectoryPins pins = new();
    private FileStream? marker;
    private bool disposed;
    private StreamingFixture() { }
    public string Id { get; private set; } = string.Empty;
    public string Root { get; private set; } = string.Empty;
    public string RootIdentity { get; private set; } = string.Empty;
    public string OraclePath { get; private set; } = string.Empty;
    public int FileCount { get; private set; }
    public int DirectoryCount { get; private set; }
    public long TotalBytes { get; private set; }

    public static async Task<StreamingFixture> CreateAsync(StreamingFixtureOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new StreamingFixtureOptions();
        if (options.FileCount is < 1 or > 25000 || options.DirectoryCount is < 1 or > 256 ||
            options.TotalBytes is < 0 or > 1073741824 || (options.TotalBytes + options.FileCount - 1) / options.FileCount > 4 * 1024 * 1024 ||
            options.MaxDuration <= TimeSpan.Zero || options.MaxDuration > TimeSpan.FromMinutes(45) || options.MinimumFreeBytes < 0)
            throw new FixtureSafetyException("streaming.invalid_generation_limits");
        var fixture = new StreamingFixture { Id = Guid.NewGuid().ToString("N"), FileCount = options.FileCount, DirectoryCount = options.DirectoryCount, TotalBytes = options.TotalBytes };
        var elapsed = Stopwatch.StartNew();
        string? session = null;
        var completed = 0;
        long written = 0;
        try
        {
            var parent = FixedParent(fixture.pins, create: true);
            session = Path.Combine(parent, fixture.Id);
            fixture.pins.CreateChildDirectory(parent, fixture.Id, false);
            fixture.Root = Path.Combine(session, "payload");
            fixture.pins.CreateChildDirectory(session, "payload", false);
            fixture.RootIdentity = fixture.pins.GetPinnedIdentity(fixture.Root);
            fixture.OraclePath = Path.Combine(session, "oracle.jsonl");
            Check(options.TotalBytes);
            var ownership = JsonSerializer.SerializeToUtf8Bytes(new StreamingFixtureOwnership(1, fixture.Id, fixture.RootIdentity));
            using (var stream = new FileStream(fixture.pins.OpenNewFile(session, ".streaming-fixture-owner"), FileAccess.ReadWrite))
            {
                stream.Write(ownership);
                stream.Flush(true);
            }
            fixture.marker = new FileStream(fixture.pins.OpenReadFile(session, ".streaming-fixture-owner"), FileAccess.Read);
            for (var directory = 0; directory < options.DirectoryCount; directory++)
            {
                Check(0);
                fixture.pins.CreateChildDirectory(fixture.Root, $"d{directory:D3}", false);
            }

            using (var oracleStream = new FileStream(fixture.pins.OpenNewFile(session, "oracle.jsonl"), FileAccess.ReadWrite))
            using (var oracle = new StreamWriter(oracleStream, new UTF8Encoding(false), 4096, leaveOpen: true))
            {
                oracle.NewLine = "\n";
                var buffer = new byte[64 * 1024];
                for (var index = 0; index < options.FileCount; index++)
                {
                    var length = options.TotalBytes / options.FileCount + (index < options.TotalBytes % options.FileCount ? 1 : 0);
                    Check(length);
                    var directory = $"d{index % options.DirectoryCount:D3}";
                    var file = $"f{index:D6}.bin";
                    var relative = directory + "/" + file;
                    using var payload = new FileStream(fixture.pins.OpenNewFile(Path.Combine(fixture.Root, directory), file), FileAccess.ReadWrite);
                    var state = unchecked(((ulong)(uint)options.Seed << 32) ^ (ulong)(index + 1) ^ 0x9E3779B97F4A7C15UL);
                    using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    for (long offset = 0; offset < length;)
                    {
                        Check(0);
                        var count = (int)Math.Min(buffer.Length, length - offset);
                        Fill(buffer.AsSpan(0, count), ref state);
                        var previousPosition = payload.Position;
                        try { await payload.WriteAsync(buffer.AsMemory(0, count), cancellationToken); }
                        finally { written = checked(written + payload.Position - previousPosition); }
                        expected.AppendData(buffer, 0, count);
                        offset += count;
                    }
                    payload.Flush(true);
                    File.SetLastWriteTimeUtc(payload.SafeFileHandle, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                    payload.Position = 0;
                    var actual = Convert.ToHexString(SHA256.HashData(payload));
                    if (actual != Convert.ToHexString(expected.GetHashAndReset())) throw new FixtureSafetyException("streaming.generated_hash_mismatch");
                    var observation = WindowsDirectoryPins.ObserveHandle(payload.SafeFileHandle, relative);
                    await oracle.WriteLineAsync(JsonSerializer.Serialize(new StreamingOracleEntry(observation, actual)));
                    completed++;
                }
                for (var directory = 0; directory < options.DirectoryCount; directory++)
                {
                    Check(0);
                    var name = $"d{directory:D3}";
                    var observation = fixture.pins.ObservePinnedDirectory(Path.Combine(fixture.Root, name), name);
                    await oracle.WriteLineAsync(JsonSerializer.Serialize(new StreamingOracleEntry(observation, string.Empty)));
                }
                await oracle.FlushAsync(cancellationToken);
                oracleStream.Flush(true);
            }
            WriteReceipt("Completed", null);
            return fixture;
        }
        catch (Exception error)
        {
            List<Exception>? additional = null;
            try { if (session is not null && !fixture.pins.ChildExists(session, "generation.json")) WriteReceipt(error is OperationCanceledException ? "Cancelled" : "Failed", error is FixtureSafetyException safety ? safety.Code : "streaming.generation_failed"); }
            catch (Exception receiptFailure) { (additional ??= []).Add(receiptFailure); }
            try { fixture.Dispose(); }
            catch (Exception cleanupFailure) { (additional ??= []).Add(cleanupFailure); }
            if (additional is not null) throw new AggregateException("Streaming generation and evidence/release failed.", new[] { error }.Concat(additional));
            throw;
        }

        void Check(long pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (elapsed.Elapsed > options.MaxDuration) throw new FixtureSafetyException("streaming.generation_deadline");
            var free = new DriveInfo(Path.GetPathRoot(fixture.Root)!).AvailableFreeSpace;
            var reserve = Math.Max(256L * 1024 * 1024, options.MinimumFreeBytes);
            if (free < reserve || pending > free - reserve) throw new FixtureSafetyException("streaming.generation_free_space");
        }
        void WriteReceipt(string outcome, string? error)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { FormatVersion = 1, fixture.Id, Outcome = outcome, FilesCompleted = completed,
                BytesWritten = written, PlannedFiles = options.FileCount, PlannedBytes = options.TotalBytes, options.Seed,
                ContentAlgorithm = "xorshift64star-le-v1", ErrorCode = error, ElapsedMilliseconds = elapsed.ElapsedMilliseconds });
            using var receipt = new FileStream(fixture.pins.OpenNewFile(session!, "generation.json"), FileAccess.ReadWrite);
            receipt.Write(bytes); receipt.Flush(true);
        }
    }

    internal static string FixedParent(WindowsDirectoryPins pins, bool create)
    {
        string? checkout = null;
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "FileOrganizer.slnx")) && File.Exists(Path.Combine(current.FullName, "tests", "FileOrganizer.Tests", "FileOrganizer.Tests.csproj")))
            { checkout = current.FullName; break; }
        if (checkout is null) throw new FixtureSafetyException("streaming.checkout_not_found");
        if (!create)
        {
            var fixedPath = Path.Combine(checkout, "artifacts", EvidenceTask, "native-fixtures");
            pins.PinAncestors(fixedPath);
            return fixedPath;
        }
        pins.PinAncestors(checkout);
        var parent = checkout;
        foreach (var segment in new[] { "artifacts", EvidenceTask, "native-fixtures" })
        { pins.CreateChildDirectory(parent, segment, true); parent = Path.Combine(parent, segment); }
        return parent;
    }

    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        pins.Validate();
        if (marker is null || pins.GetPinnedIdentity(Root) != RootIdentity) throw new FixtureSafetyException("streaming.fixture_identity_changed");
    }

    public async Task<StreamingFixtureVerification> VerifyUnchangedAsync(CancellationToken cancellationToken = default)
    {
        Validate();
        var elapsed = Stopwatch.StartNew();
        var files = 0;
        var directories = 0;
        long bytes = 0;
        using var oracle = new FileStream(pins.OpenReadFile(Path.GetDirectoryName(Root)!, "oracle.jsonl"), FileAccess.Read);
        if (oracle.Length > 64L * 1024 * 1024) throw new FixtureSafetyException("streaming.oracle_size_limit");
        await foreach (var line in ReadOracleLines(oracle, cancellationToken))
        {
                cancellationToken.ThrowIfCancellationRequested();
                if (elapsed.Elapsed > TimeSpan.FromMinutes(45) || line.Length > 4096 || files + directories >= 25256)
                    throw new FixtureSafetyException("streaming.verification_budget");
                var expected = JsonSerializer.Deserialize<StreamingOracleEntry>(line) ?? throw new FixtureSafetyException("streaming.invalid_oracle");
                var relative = expected.Observation.RelativePath;
                if (relative.Length > 100 || relative.Contains('\\') || relative.Contains(':') || relative.Split('/').Any(value => value is "" or "." or ".."))
                    throw new FixtureSafetyException("streaming.invalid_oracle_path");
                var path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
                InventoryEntry observed;
                if (expected.Observation.Kind == InventoryEntryKind.Directory)
                {
                    observed = pins.ObservePinnedDirectory(path, relative);
                    directories++;
                }
                else
                {
                    using var payload = new FileStream(pins.OpenReadFile(Path.GetDirectoryName(path)!, Path.GetFileName(path)), FileAccess.Read);
                    var digest = Convert.ToHexString(await SHA256.HashDataAsync(payload, cancellationToken));
                    if (digest != expected.Sha256) throw new FixtureSafetyException("streaming.payload_hash_changed");
                    observed = WindowsDirectoryPins.ObserveHandle(payload.SafeFileHandle, relative);
                    files++;
                    bytes = checked(bytes + observed.Length);
                }
                if (observed != expected.Observation) throw new FixtureSafetyException("streaming.payload_metadata_changed");
        }
        if (files != FileCount || directories != DirectoryCount || bytes != TotalBytes)
            throw new FixtureSafetyException("streaming.oracle_totals_mismatch");
        oracle.Position = 0;
        var oracleHash = Convert.ToHexString(await SHA256.HashDataAsync(oracle, cancellationToken));
        Validate();
        return new(files, directories, bytes, oracleHash, elapsed.ElapsedMilliseconds);
    }

    internal async IAsyncEnumerable<StreamingOracleEntry> ReadOracleAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Validate();
        using var oracle = new FileStream(pins.OpenReadFile(Path.GetDirectoryName(Root)!, "oracle.jsonl"), FileAccess.Read);
        if (oracle.Length > 64L * 1024 * 1024) throw new FixtureSafetyException("streaming.oracle_size_limit");
        var count = 0;
        await foreach (var line in ReadOracleLines(oracle, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > FileCount + DirectoryCount) throw new FixtureSafetyException("streaming.oracle_count_limit");
            yield return JsonSerializer.Deserialize<StreamingOracleEntry>(line) ?? throw new FixtureSafetyException("streaming.invalid_oracle");
        }
        if (count != FileCount + DirectoryCount) throw new FixtureSafetyException("streaming.oracle_totals_mismatch");
        Validate();
    }

    private static async IAsyncEnumerable<string> ReadOracleLines(Stream stream, [EnumeratorCancellation] CancellationToken token)
    {
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true, 4096, leaveOpen: true);
        var buffer = new char[1024];
        var line = new StringBuilder(512);
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), token);
            if (count == 0) break;
            for (var index = 0; index < count; index++)
            {
                if (buffer[index] == '\n')
                {
                    yield return line.ToString();
                    line.Clear();
                }
                else
                {
                    if (line.Length >= 4096) throw new FixtureSafetyException("streaming.oracle_line_limit");
                    line.Append(buffer[index]);
                }
            }
        }
        if (line.Length != 0) throw new FixtureSafetyException("streaming.oracle_incomplete_line");
    }

    private static void Fill(Span<byte> bytes, ref ulong state)
    {
        for (var offset = 0; offset < bytes.Length;)
        {
            state ^= state >> 12; state ^= state << 25; state ^= state >> 27;
            var value = unchecked(state * 2685821657736338717UL);
            var count = Math.Min(8, bytes.Length - offset);
            if (count == 8) BinaryPrimitives.WriteUInt64LittleEndian(bytes.Slice(offset, count), value);
            else for (var index = 0; index < count; index++) bytes[offset + index] = (byte)(value >> (index * 8));
            offset += count;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try { marker?.Dispose(); } finally { pins.Dispose(); }
    }
}
