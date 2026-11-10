namespace FileOrganizer.Application;

public interface IInventoryCaptureSource : IInventorySource
{
    Task<IInventoryCaptureLease> OpenReadLeaseAsync(CancellationToken cancellationToken);
}

public interface IInventoryCaptureLease : IAsyncDisposable
{
    string RootIdentity { get; }
    Task<IInventoryDirectoryReader> OpenDirectoryAsync(string relativePath, string expectedIdentity, CancellationToken cancellationToken);
}

public interface IInventoryDirectoryReader : IAsyncDisposable
{
    string DirectoryIdentity { get; }
    Task<InventoryCapturePage> ReadPageAsync(int pageSize, CancellationToken cancellationToken);
}

public sealed record InventoryCapturePage(IReadOnlyList<InventoryEntry> Entries, bool EndOfDirectory);
public enum InventoryCapturePhase { Capturing, Verifying, Completed, Incomplete }
public sealed record InventoryCaptureProgress(string GenerationId, InventoryCapturePhase Phase,
    int CapturedEntries, int CompletedDirectories, int PendingDirectories, int VerifiedEntries);

public sealed record InventoryCaptureOptions
{
    public int PageSize { get; init; } = 256;
    public int MaxEntries { get; init; } = 20_000;
    public int MaxDirectories { get; init; } = 2_000;
    public int MaxDepth { get; init; } = 16;
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(15);
    public long MaxDatabaseBytes { get; init; } = 2L * 1024 * 1024 * 1024;
    public long MaxWalBytes { get; init; } = 512L * 1024 * 1024;
    public long MinimumFreeBytes { get; init; } = 12L * 1024 * 1024 * 1024;
    public long MaxResidentBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaxRunBytes { get; init; } = 6L * 1024 * 1024 * 1024;

    public void Validate()
    {
        if (PageSize is < 1 or > 1000 || MaxEntries is < 1 or > 1_100_000 ||
            MaxDirectories is < 1 or > 12_000 || MaxDepth is < 1 or > 32 ||
            MaxDuration <= TimeSpan.Zero || MaxDuration > TimeSpan.FromMinutes(45) ||
            MaxDatabaseBytes is < 1 or > 2L * 1024 * 1024 * 1024 ||
            MaxWalBytes is < 1 or > 512L * 1024 * 1024 ||
            MinimumFreeBytes < 0 || MaxResidentBytes is < 1 or > 1024L * 1024 * 1024 ||
            MaxRunBytes is < 1 or > 6L * 1024 * 1024 * 1024)
            throw new InventoryIndexException("inventory.capture_invalid_options");
    }
}
