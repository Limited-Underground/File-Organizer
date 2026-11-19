using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Xunit;
using Xunit.Abstractions;

namespace FileOrganizer.Tests;

public sealed class FixtureBoundaryTests(ITestOutputHelper output)
{
    private static readonly DateTime FixedTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void UnboundedOrOverflowSizedLimitsCannotCreateWorkspace()
    {
        FixtureLimits[] invalid =
        [
            new() { MaxFiles = int.MaxValue },
            new() { MaxDirectories = int.MaxValue },
            new() { MaxFileBytes = long.MaxValue },
            new() { MaxTotalBytes = long.MaxValue },
            new() { MaxDepth = int.MaxValue },
            new() { MaxDuration = TimeSpan.MaxValue },
        ];
        foreach (var limits in invalid)
            Assert.Throws<FixtureSafetyException>(() => FixtureWorkspace.Create(limits));
    }

    [Fact]
    public void InsufficientFreeSpaceBudgetFailsWithoutAllocatingPayload()
    {
        var error = Assert.Throws<FixtureSafetyException>(() =>
            FixtureWorkspace.Create(new() { MinimumFreeBytes = long.MaxValue }));
        Assert.Equal("quota.free_space", error.Code);
    }

    [Theory]
    [InlineData("C:/outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("../sibling-prefix/file.bin")]
    [InlineData("nested/../../outside.bin")]
    [InlineData("nested\\file.bin")]
    [InlineData("file.bin:alternate")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("COM1.log")]
    [InlineData("LPT9")]
    [InlineData("folder/.")]
    [InlineData("folder//file")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    public void UnsafeRelativePathsAreRejectedWithoutCreation(string relative)
    {
        using var workspace = FixtureWorkspace.Create();
        Assert.Throws<FixtureSafetyException>(() => workspace.ResolveOwnedPath(relative));
        Assert.Single(Directory.EnumerateFileSystemEntries(workspace.Root));
    }

    [Fact]
    public void SiblingWithMatchingRootPrefixIsOutsideOwnership()
    {
        using var workspace = FixtureWorkspace.Create();
        var relative = "../" + Path.GetFileName(workspace.Root) + "-sibling/file.bin";
        Assert.Throws<FixtureSafetyException>(() => workspace.ResolveOwnedPath(relative));
    }

    [Fact]
    public void ExistingUnownedLeafIsNeverOverwritten()
    {
        using var workspace = FixtureWorkspace.Create();
        var leaf = Path.Combine(workspace.Root, "existing.bin");
        File.WriteAllBytes(leaf, [11, 22, 33]);
        Assert.Throws<FixtureSafetyException>(() => workspace.CreatePayload("existing.bin", 1));
        Assert.Equal(new byte[] { 11, 22, 33 }, File.ReadAllBytes(leaf));
    }

    [Fact]
    public async Task ExistingHardLinkCannotBeOpenedAsNewPayloadOrAlterSentinel()
    {
        var sentinel = await MakeReleasedSentinel();
        using (var exclusive = new FileStream(sentinel.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var observed = new byte[sentinel.Bytes.Length];
            exclusive.ReadExactly(observed);
            Assert.Equal(sentinel.Bytes, observed);
        }
        // Establish an actual hard link while its task-owned destination directory is released.
        // This separates native capability from injection into a currently pinned directory.
        var releasedAlias = Path.Combine(sentinel.Root, "released-hard-link.bin");
        var realLinkCreated = await TryCreateHardLinkWithBoundedRetry(releasedAlias, sentinel.File);
        Assert.True(realLinkCreated.Success, $"Released-tree hard-link creation failed: {realLinkCreated.Error}");
        Assert.Equal(sentinel.Bytes, File.ReadAllBytes(releasedAlias));
        using var workspace = FixtureWorkspace.Create();
        var link = Path.Combine(workspace.Root, "hard-link.bin");
        var injection = await TryCreateHardLinkWithBoundedRetry(link, sentinel.File);
        if (injection.Success)
        {
            output.WriteLine("Actual hard-link injection into active fixture succeeded; CreatePayload must reject the existing unowned alias.");
            Assert.Throws<FixtureSafetyException>(() => workspace.CreatePayload("hard-link.bin", 3));
        }
        else
        {
            Assert.Equal(32, injection.Error);
            Assert.False(File.Exists(link));
            output.WriteLine("Hard-link capability exercised in released task-owned tree. Active-root injection blocked with ERROR_SHARING_VIOLATION (32) after bounded retry; existing-unowned-hard-link opening was not exercised in this branch. No guards were relaxed.");
        }
        Assert.Throws<FixtureSafetyException>(() => workspace.ResolveOwnedPath(releasedAlias));
        Assert.Throws<FixtureSafetyException>(() => workspace.ResolveOwnedPath("../" + Path.GetFileName(sentinel.Root) + "/released-hard-link.bin"));
        Assert.Equal(sentinel.Bytes, File.ReadAllBytes(sentinel.File));
        Assert.Equal(sentinel.Bytes, File.ReadAllBytes(releasedAlias));
    }

    [Fact]
    public async Task RealJunctionToOwnedSiblingIsRejectedAndSentinelRemainsUntouched()
    {
        var sentinel = await MakeReleasedSentinel();
        using var workspace = FixtureWorkspace.Create();
        var junction = Path.Combine(workspace.Root, "junction");
        CreateJunction(junction, sentinel.Root);
        Assert.True(File.GetAttributes(junction).HasFlag(FileAttributes.ReparsePoint));
        Assert.Throws<FixtureSafetyException>(() => workspace.ResolveOwnedPath("junction/sentinel.bin"));
        Assert.Throws<FixtureSafetyException>(() => workspace.CreatePayload("junction/sentinel.bin", 3));
        Assert.Equal(sentinel.Bytes, File.ReadAllBytes(sentinel.File));
    }

    [DeveloperModeSymlinkFact]
    public async Task RealSymlinkToOwnedSiblingIsRejectedAndSentinelRemainsUntouched()
    {
        var sentinel = await MakeReleasedSentinel();
        using var workspace = FixtureWorkspace.Create();
        var link = Path.Combine(workspace.Root, "symlink");
        Directory.CreateSymbolicLink(link, sentinel.Root);
        Assert.Throws<FixtureSafetyException>(() => workspace.ResolveOwnedPath("symlink/sentinel.bin"));
        Assert.Throws<FixtureSafetyException>(() => workspace.CreatePayload("symlink/sentinel.bin", 3));
        Assert.Equal(sentinel.Bytes, File.ReadAllBytes(sentinel.File));
    }

    [Fact]
    public async Task PinnedRootCannotBeRenamedAndDescendantRebindingCannotEscapeOwnership()
    {
        var sentinel = await MakeReleasedSentinel();
        using var workspace = FixtureWorkspace.Create();
        Assert.ThrowsAny<IOException>(() => Directory.Move(workspace.Root, workspace.Root + "-moved"));
        workspace.EnsureDirectory("empty");
        var rebound = TrySetJunction(Path.Combine(workspace.Root, "empty"), sentinel.Root, out var observation, out _);
        output.WriteLine("Pinned descendant metadata-rebind observation: " + observation);
        if (rebound)
        {
            Assert.Throws<FixtureSafetyException>(() => workspace.ResolveOwnedPath("empty/sentinel.bin"));
            Assert.Throws<FixtureSafetyException>(() => workspace.EnsureDirectory("empty/new-directory"));
            Assert.Throws<FixtureSafetyException>(() => workspace.CreatePayload("empty/new.bin", 1));
        }
        else
        {
            workspace.ValidateOwnership();
            Assert.False(File.GetAttributes(Path.Combine(workspace.Root, "empty")).HasFlag(FileAttributes.ReparsePoint));
        }
        Assert.Equal(sentinel.Bytes, File.ReadAllBytes(sentinel.File));
        Assert.False(File.Exists(Path.Combine(sentinel.Root, "new.bin")));
        Assert.False(Directory.Exists(Path.Combine(sentinel.Root, "new-directory")));
    }

    [Fact]
    public async Task CheckpointRebindingCannotRedirectGeneratorPayloadOutsideOwnedDirectory()
    {
        var sentinel = await MakeReleasedSentinel();
        using var workspace = FixtureWorkspace.Create();
        var attempted = false;
        var rebound = false;
        var observation = string.Empty;
        var generator = new FixtureGenerator((point, _) =>
        {
            if (point != FixtureCheckpoint.BeforeFileCreate || attempted) return;
            attempted = true;
            rebound = TrySetJunction(Path.Combine(workspace.Root, "empty"), sentinel.Root, out observation, out _);
        });
        var error = await Record.ExceptionAsync(() => generator.GenerateAsync(workspace,
            new(3, [new("empty/new.bin", 32, "rebind")] )));
        Assert.True(attempted);
        output.WriteLine("BeforeFileCreate metadata-rebind observation: " + observation);
        if (rebound) Assert.NotNull(error);
        else Assert.Null(error);
        Assert.Equal(sentinel.Bytes, File.ReadAllBytes(sentinel.File));
        Assert.False(File.Exists(Path.Combine(sentinel.Root, "new.bin")));
    }

    [Fact]
    public void OwnershipMarkerCannotBeOverwrittenOrDeletedWhileWorkspaceIsActive()
    {
        using var workspace = FixtureWorkspace.Create();
        var marker = Path.Combine(workspace.Root, ".file-organizer-fixture-owner");
        var original = ReadShared(marker);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(marker, "tampered"));
        Assert.ThrowsAny<IOException>(() => File.Delete(marker));
        workspace.ValidateOwnership();
        Assert.Equal(original, ReadShared(marker));
    }

    [Fact]
    public void DisposedWorkspaceRejectsFurtherReadsAndMutations()
    {
        var workspace = FixtureWorkspace.Create();
        workspace.Dispose();
        Assert.Throws<ObjectDisposedException>(() => workspace.ResolveOwnedPath("new.bin"));
        Assert.Throws<ObjectDisposedException>(() => workspace.EnsureDirectory("new-directory"));
        Assert.Throws<ObjectDisposedException>(() => workspace.CreatePayload("new.bin", 0));
        Assert.Throws<ObjectDisposedException>(() => workspace.ReadOwnedBytes("receipt.json"));
    }

    [Fact]
    public async Task WriterCannotExceedDeclaredLengthOrWriteAfterCompletion()
    {
        using var workspace = FixtureWorkspace.Create();
        var writer = workspace.CreatePayload("bounded.bin", 2);
        await Assert.ThrowsAsync<FixtureSafetyException>(async () => await writer.WriteAsync(new byte[3], CancellationToken.None));
        await writer.WriteAsync(new byte[] { 1, 2 }, CancellationToken.None);
        await writer.CompleteAsync(FixedTime, CancellationToken.None);
        await Assert.ThrowsAsync<FixtureSafetyException>(async () => await writer.WriteAsync(new byte[] { 3 }, CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2 }, workspace.ReadOwnedBytes("bounded.bin"));
    }

    private static async Task<(string Root, string File, byte[] Bytes)> MakeReleasedSentinel()
    {
        using var workspace = FixtureWorkspace.Create();
        byte[] bytes = [41, 42, 43];
        var writer = workspace.CreatePayload("sentinel.bin", bytes.Length);
        await writer.WriteAsync(bytes, CancellationToken.None);
        await writer.CompleteAsync(FixedTime, CancellationToken.None);
        return (workspace.Root, Path.Combine(workspace.Root, "sentinel.bin"), bytes);
    }

    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static async Task<(bool Success, int Error)> TryCreateHardLinkWithBoundedRetry(string alias, string source)
    {
        var error = 0;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            if (CreateHardLink(alias, source, IntPtr.Zero)) return (true, 0);
            error = Marshal.GetLastWin32Error();
            if (error != 32) break;
            if (attempt < 4) await Task.Delay(25);
        }
        return (false, error);
    }

    private static void CreateJunction(string path, string target)
    {
        Directory.CreateDirectory(path);
        if (!TrySetJunction(path, target, out var observation, out var error))
            throw new Win32Exception(error, observation);
    }

    private static bool TrySetJunction(string path, string target, out string observation, out int lastError)
    {
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
        var attempts = new List<string>();
        lastError = 0;
        foreach (var access in new uint[] { 0x100, 0x40000000, 0 })
        {
            using var handle = CreateFile(path, access, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                lastError = Marshal.GetLastWin32Error();
                attempts.Add($"access=0x{access:X}:open-rejected:{lastError}");
                continue;
            }
            if (DeviceIoControl(handle, 0x000900A4, bytes, bytes.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                attempts.Add($"access=0x{access:X}:FSCTL_SET_REPARSE_POINT-succeeded");
                observation = string.Join(";", attempts);
                return true;
            }
            lastError = Marshal.GetLastWin32Error();
            attempts.Add($"access=0x{access:X}:FSCTL_SET_REPARSE_POINT-rejected:{lastError}");
        }
        observation = string.Join(";", attempts);
        return false;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int bytesReturned, IntPtr overlapped);
}

/// <summary>Conservative discovery-time skip; no privilege or machine setting is changed.</summary>
public sealed class DeveloperModeSymlinkFactAttribute : FactAttribute
{
    public DeveloperModeSymlinkFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Windows symlink capability test requires Windows; coverage not exercised.";
            return;
        }

        try
        {
            if (Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock", "AllowDevelopmentWithoutDevLicense", null) is not int value || value != 1)
                Skip = "Unprivileged symlink capability unavailable: Developer Mode is not enabled; no privilege enabling attempted; symlink coverage not exercised.";
        }
        catch (System.Security.SecurityException)
        {
            Skip = "Cannot establish unprivileged symlink capability from read-only policy; symlink coverage not exercised.";
        }
    }
}
