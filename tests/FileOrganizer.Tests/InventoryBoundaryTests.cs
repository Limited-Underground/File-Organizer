using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FileOrganizer.Application;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class InventoryBoundaryTests
{
    [Fact]
    public async Task CaptureTraversesMultipleNativeBuffersAndRestartsEnumeration()
    {
        using var fixture = FixtureWorkspace.Create(new FixtureLimits { MaxFiles = 128 });
        var specs = Enumerable.Range(0, 100).Select(index =>
            new FixtureFileSpec($"{index:D3}-{new string('x', 84)}.bin", 1, $"content-{index}")).ToArray();
        await new FixtureGenerator().GenerateAsync(fixture, new(21, specs));
        var source = new FixtureInventorySource(fixture);
        var first = await source.CaptureAsync(CancellationToken.None);
        var second = await source.CaptureAsync(CancellationToken.None);
        Assert.Equal(100, first.Entries.Count);
        Assert.Equal(specs.Select(spec => spec.RelativePath).Order(StringComparer.Ordinal),
            first.Entries.Select(entry => entry.RelativePath));
        Assert.Equal(PreparedInventorySnapshot.Create(first).Fingerprint, PreparedInventorySnapshot.Create(second).Fingerprint);
    }

    [Fact]
    public async Task CaptureDiscoversUnplannedEntriesAndLeavesPayloadBytesAndMetadataUnchanged()
    {
        using var fixture = FixtureWorkspace.Create();
        await new FixtureGenerator().GenerateAsync(fixture, new(14, [new("nested/planned.bin", 97, "payload")]));
        var before = fixture.ReadOwnedBytes("nested/planned.bin");
        var modified = File.GetLastWriteTimeUtc(Path.Combine(fixture.Root, "nested", "planned.bin"));
        // This new file belongs only to this disposable test, and deliberately is not in the oracle.
        var unplanned = Path.Combine(fixture.Root, "unplanned.bin");
        File.WriteAllBytes(unplanned, [9, 8, 7]);
        var source = new FixtureInventorySource(fixture);
        var first = await source.CaptureAsync(CancellationToken.None);
        var second = await source.CaptureAsync(CancellationToken.None);
        Assert.Contains(first.Entries, entry => entry.RelativePath == "unplanned.bin" && entry.Length == 3);
        Assert.Contains(first.Entries, entry => entry.RelativePath == "nested" && entry.Kind == InventoryEntryKind.Directory);
        Assert.DoesNotContain(first.Entries, entry => entry.RelativePath is "manifest.json" or "receipt.json" or ".file-organizer-fixture-owner");
        Assert.Equal(PreparedInventorySnapshot.Create(first).Fingerprint, PreparedInventorySnapshot.Create(second).Fingerprint);
        Assert.Equal(before, fixture.ReadOwnedBytes("nested/planned.bin"));
        Assert.Equal(modified, File.GetLastWriteTimeUtc(Path.Combine(fixture.Root, "nested", "planned.bin")));
        Assert.Equal(new byte[] { 9, 8, 7 }, File.ReadAllBytes(unplanned));
    }

    [Fact]
    public async Task CaptureAccountsForJunctionWithoutFollowingIt()
    {
        using var sibling = FixtureWorkspace.Create();
        await new FixtureGenerator().GenerateAsync(sibling, new(9, [new("sentinel.bin", 53, "sentinel")]));
        var expected = sibling.ReadOwnedBytes("sentinel.bin");
        using var fixture = FixtureWorkspace.Create();
        await new FixtureGenerator().GenerateAsync(fixture, new(2, [new("local.bin", 7, "local")]));
        CreateJunction(Path.Combine(fixture.Root, "outside"), sibling.Root);
        var snapshot = await new FixtureInventorySource(fixture).CaptureAsync(CancellationToken.None);
        var exclusion = Assert.Single(snapshot.Entries, entry => entry.RelativePath == "outside");
        Assert.Equal(InventoryEntryKind.ExcludedReparse, exclusion.Kind);
        Assert.DoesNotContain(snapshot.Entries, entry => entry.RelativePath.StartsWith("outside/", StringComparison.Ordinal));
        Assert.Equal(expected, sibling.ReadOwnedBytes("sentinel.bin"));
    }

    [DeveloperModeSymlinkFact]
    public async Task CaptureAccountsForSymlinkWithoutFollowingIt()
    {
        using var sibling = FixtureWorkspace.Create();
        await new FixtureGenerator().GenerateAsync(sibling, new(7, [new("sentinel.bin", 2, "sentinel")]));
        using var fixture = FixtureWorkspace.Create();
        await new FixtureGenerator().GenerateAsync(fixture, new(7, []));
        Directory.CreateSymbolicLink(Path.Combine(fixture.Root, "outside"), sibling.Root);
        var snapshot = await new FixtureInventorySource(fixture).CaptureAsync(CancellationToken.None);
        Assert.Equal(InventoryEntryKind.ExcludedReparse, Assert.Single(snapshot.Entries).Kind);
    }

    [Fact]
    public async Task CaptureCancellationAndDisposedRootRefuseFurtherWork()
    {
        using var fixture = FixtureWorkspace.Create();
        await new FixtureGenerator().GenerateAsync(fixture, new(7, []));
        var source = new FixtureInventorySource(fixture);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.CaptureAsync(cancellation.Token));
        fixture.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => source.CaptureAsync(CancellationToken.None));
    }

    internal static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var print = Encoding.Unicode.GetBytes(target);
        var bytes = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(bytes, 0);
        BitConverter.GetBytes(checked((ushort)(bytes.Length - 8))).CopyTo(bytes, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(bytes, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(bytes, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(bytes, 14);
        substitute.CopyTo(bytes, 16);
        print.CopyTo(bytes, 16 + substitute.Length + 2);
        using var handle = CreateFile(path, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint shareMode,
        IntPtr securityAttributes, uint disposition, uint flags, IntPtr templateFile);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
}
