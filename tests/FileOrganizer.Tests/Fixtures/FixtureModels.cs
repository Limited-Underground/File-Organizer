namespace FileOrganizer.Tests.Fixtures;

internal sealed record FixtureLimits
{
    public int MaxFiles { get; init; } = 64;
    public int MaxDirectories { get; init; } = 16;
    public long MaxFileBytes { get; init; } = 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 8 * 1024 * 1024;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromSeconds(15);
    public long MinimumFreeBytes { get; init; } = 64 * 1024 * 1024;
    public int MaxDepth { get; init; } = 8;

    public void Validate()
    {
        if (MaxFiles is < 1 or > 256 || MaxDirectories is < 1 or > 64 ||
            MaxFileBytes is < 0 or > 4 * 1024 * 1024 || MaxTotalBytes is < 0 or > 32 * 1024 * 1024 ||
            MaxDuration <= TimeSpan.Zero || MaxDuration > TimeSpan.FromSeconds(60) ||
            MinimumFreeBytes < 0 || MaxDepth is < 1 or > 16)
            throw new FixtureSafetyException("fixture.invalid_limits");
    }
}

internal sealed class FixtureSafetyException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

internal sealed record FixtureFileSpec(string RelativePath, long Length, string ContentKey);
internal sealed record FixturePlan(int Seed, IReadOnlyList<FixtureFileSpec> Files, IReadOnlyList<string>? Directories = null);
internal enum FixtureCheckpoint
{
    BeforeManifest, AfterManifest, BeforeDirectoryCreate, AfterDirectoryCreate,
    BeforeFileCreate, AfterFileCreate, AfterChunkWrite, AfterFileComplete, BeforeReceipt
}
internal enum FixtureFaultAction { Throw, Cancel }
internal sealed record FixtureFault(FixtureCheckpoint Point, int Occurrence, FixtureFaultAction Action);
internal sealed class FixtureInjectedException(FixtureCheckpoint point, int occurrence) : IOException("fixture.injected_fault")
{
    public FixtureCheckpoint Point { get; } = point;
    public int Occurrence { get; } = occurrence;
}
internal sealed record FixtureManifestEntry(string RelativePath, long Length, string ContentKey, string Sha256, DateTime ModifiedUtc);
internal sealed record FixtureManifest(int FormatVersion, string ContentAlgorithm, int Seed, IReadOnlyList<string> Directories, IReadOnlyList<FixtureManifestEntry> Files);
internal sealed record FixtureReceipt(string RunId, string Outcome, int FilesCreated, int FilesCompleted, long BytesWritten,
    string? ManifestSha256, string? LastCheckpoint, int CheckpointOccurrence, string? ErrorCode);
internal sealed record FixtureRunResult(byte[] CanonicalManifestBytes, FixtureReceipt Receipt);
