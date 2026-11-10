using System.Globalization;
using FileOrganizer.Application;

namespace FileOrganizer.Tests;

/// <summary>Versioned arithmetic metadata only; never enumerates or creates physical file payloads.</summary>
internal sealed class ArithmeticInventorySource(int rootOrdinal) : IInventoryCaptureSource
{
    public const int Seed = 104729;
    public const int RootCount = 100;
    public const int DirectoriesPerRoot = 100;
    public const int FileEntriesPerRoot = 9900;
    public const int FileObjectsPerRoot = 9800;
    public int RootOrdinal { get; } = rootOrdinal is >= 0 and < RootCount ? rootOrdinal : throw new ArgumentOutOfRangeException(nameof(rootOrdinal));
    public bool IsOffline => RootOrdinal < 10;
    public string RootIdentity => $"capacity-r{RootOrdinal:D3}-{(IsOffline ? "offline" : "online")}";

    public Task<InventorySnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("capacity.streaming_pipeline_required");

    public Task<IInventoryCaptureLease> OpenReadLeaseAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IInventoryCaptureLease>(new Lease(this));
    }

    private InventoryEntry Directory(int directory) => new($"d{directory:D3}", InventoryEntryKind.Directory,
        $"capacity:r{RootOrdinal:D3}:d{directory:D3}", 0, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), (long)FileAttributes.Directory);

    private InventoryEntry File(int entryOrdinal)
    {
        var objectOrdinal = entryOrdinal < FileObjectsPerRoot ? entryOrdinal : entryOrdinal - FileObjectsPerRoot;
        var number = (long)RootOrdinal * FileObjectsPerRoot + objectOrdinal;
        var extension = ((number + Seed) % 5) switch { 0 => "txt", 1 => "bin", 2 => "jpg", 3 => "log", _ => "dat" };
        var sizeCase = (number + Seed) % 17;
        var length = sizeCase switch
        {
            0 => 0L, 1 => 1L, 2 => 4095L, 3 => 4096L, 4 => 4097L, 5 => 1048576L,
            _ => ((long)RootOrdinal * 73856093 + (long)objectOrdinal * 19349663 + Seed) % 65536,
        };
        var name = entryOrdinal < FileObjectsPerRoot ? $"item-{entryOrdinal / 100:D4}.{extension}" : $"alias-{objectOrdinal:D4}.{extension}";
        return new($"d{entryOrdinal % DirectoriesPerRoot:D3}/{name}", InventoryEntryKind.File,
            $"capacity:r{RootOrdinal:D3}:o{objectOrdinal:D5}", length,
            new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(number * 17 % 31536000), (long)FileAttributes.Normal);
    }

    private sealed class Lease(ArithmeticInventorySource source) : IInventoryCaptureLease
    {
        private bool disposed;
        public string RootIdentity => source.RootIdentity;
        public Task<IInventoryDirectoryReader> OpenDirectoryAsync(string relativePath, string expectedIdentity, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var directory = -1;
            var identity = RootIdentity;
            if (relativePath.Length != 0)
            {
                if (relativePath.Length != 4 || relativePath[0] != 'd' ||
                    !int.TryParse(relativePath.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out directory) ||
                    directory is < 0 or >= DirectoriesPerRoot || relativePath != $"d{directory:D3}")
                    throw new InventoryIndexException("capacity.invalid_directory");
                identity = source.Directory(directory).ObjectIdentity;
            }
            if (identity != expectedIdentity) throw new InventoryIndexException("capacity.identity_mismatch");
            return Task.FromResult<IInventoryDirectoryReader>(new Reader(source, directory, identity));
        }
        public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Reader(ArithmeticInventorySource source, int directory, string identity) : IInventoryDirectoryReader
    {
        private int cursor;
        private bool disposed;
        public string DirectoryIdentity => identity;
        public Task<InventoryCapturePage> ReadPageAsync(int pageSize, CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (pageSize is < 1 or > 1000) throw new InventoryIndexException("capacity.invalid_page_size");
            var total = directory < 0 ? DirectoriesPerRoot : FileEntriesPerRoot / DirectoriesPerRoot;
            var count = Math.Min(pageSize, total - cursor);
            var entries = new InventoryEntry[count];
            for (var index = 0; index < count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                entries[index] = directory < 0 ? source.Directory(cursor + index) : source.File((cursor + index) * DirectoriesPerRoot + directory);
            }
            cursor += count;
            return Task.FromResult(new InventoryCapturePage(entries, cursor == total));
        }
        public ValueTask DisposeAsync() { disposed = true; return ValueTask.CompletedTask; }
    }
}
