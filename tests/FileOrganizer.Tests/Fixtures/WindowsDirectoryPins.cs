using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using FileOrganizer.Application;
using Microsoft.Win32.SafeHandles;

namespace FileOrganizer.Tests.Fixtures;

/// <summary>
/// Retains directory identities. Every descendant open/create is a single leaf relative to its
/// retained parent handle; sharing flags alone do not prevent reparse metadata changes.
/// </summary>
internal sealed class WindowsDirectoryPins : IDisposable
{
    private const uint DirectoryAccess = 0x001000A1;
    private const uint DirectoryOptions = 0x00200021;
    private const uint OpenReparseSync = 0x00200020;
    private readonly Dictionary<string, (SafeFileHandle Handle, FileInformation Identity)> handles = new(StringComparer.OrdinalIgnoreCase);

    public void PinAncestors(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath) ?? throw new FixtureSafetyException("directory.invalid_root");
        if (root.StartsWith("\\\\", StringComparison.Ordinal))
        {
            throw new FixtureSafetyException("directory.unc_forbidden");
        }

        PinVolumeRoot(root);
        var parent = root;
        foreach (var leaf in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            OpenDirectory(parent, leaf, 1);
            parent = Path.Combine(parent, leaf);
        }
    }

    public void CreateChildDirectory(string parentFullPath, string name, bool allowExisting)
        => OpenDirectory(parentFullPath, name, allowExisting ? 3u : 2u);

    private void OpenDirectory(string parentFullPath, string name, uint disposition)
    {
        ValidateLeaf(name);
        var fullPath = Path.Combine(parentFullPath, name);
        if (handles.ContainsKey(fullPath))
        {
            if (disposition == 2)
            {
                throw new FixtureSafetyException("directory.already_owned");
            }

            return;
        }

        var handle = OpenRelative(parentFullPath, name, DirectoryAccess, disposition, DirectoryOptions, out _);
        AddDirectory(fullPath, handle);
    }

    public SafeFileHandle OpenNewFile(string parentFullPath, string name)
    {
        var handle = OpenRelative(parentFullPath, name, 0xC0100000, 2, 0x00200060, out _);
        try
        {
            ValidateRegularFile(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public SafeFileHandle OpenReadFile(string parentFullPath, string name)
    {
        var handle = OpenRelative(parentFullPath, name, 0x80100000, 1, 0x00200060, out _);
        try
        {
            ValidateRegularFile(handle);
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public bool ChildExists(string parentFullPath, string name)
    {
        var handle = OpenRelative(parentFullPath, name, 0x00100080, 1, OpenReparseSync, out var status, allowMissing: true);
        if (IsMissing(status))
        {
            handle.Dispose();
            return false;
        }

        using (handle)
        {
            var info = ReadInformation(handle);
            if ((info.Attributes & 0x400) != 0)
            {
                throw new FixtureSafetyException("path.reparse");
            }

            return true;
        }
    }

    internal string GetPinnedIdentity(string path)
    {
        if (!handles.TryGetValue(path, out var entry)) throw new FixtureSafetyException("directory.not_pinned");
        ValidateIdentity(entry);
        return ReadNativeIdentity(entry.Handle);
    }

    internal InventoryEntry ObservePinnedDirectory(string path, string relative)
    {
        if (!handles.TryGetValue(path, out var entry)) throw new FixtureSafetyException("directory.not_pinned");
        ValidateIdentity(entry);
        return ObserveHandle(entry.Handle, relative);
    }

    internal static InventoryEntry ObserveHandle(SafeFileHandle handle, string relative)
    {
        var info = ReadInformation(handle);
        if ((info.Attributes & 0x400) != 0) throw new FixtureSafetyException("inventory.handle_reparse");
        var directory = (info.Attributes & 0x10) != 0;
        var length = directory ? 0 : checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow));
        var modified = DateTime.FromFileTimeUtc(((long)(uint)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime);
        return new InventoryEntry(relative, directory ? InventoryEntryKind.Directory : InventoryEntryKind.File,
            ReadNativeIdentity(handle), length, modified, info.Attributes);
    }

    internal IInventoryDirectoryReader OpenStreamingReader(string root, string relative, string expectedIdentity, Action validate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        validate();
        if (!handles.TryGetValue(Path.GetDirectoryName(root)!, out var parent)) throw new FixtureSafetyException("inventory.parent_not_pinned");
        SafeFileHandle? current = OpenInventoryChild(parent.Handle, Path.GetFileName(root), true);
        try
        {
            if (relative.Length != 0)
            {
                if (relative.Length > 240 || relative.Contains('\\') || relative.Contains(':')) throw new FixtureSafetyException("inventory.invalid_directory_path");
                var segments = relative.Split('/');
                if (segments.Length > 32) throw new FixtureSafetyException("inventory.directory_depth");
                foreach (var segment in segments)
                {
                    token.ThrowIfCancellationRequested();
                    ValidateLeaf(segment);
                    var next = OpenInventoryChild(current, segment, true);
                    current.Dispose();
                    current = next;
                }
            }
            var actual = ReadNativeIdentity(current);
            if (!StringComparer.Ordinal.Equals(actual, expectedIdentity)) throw new FixtureSafetyException("inventory.directory_identity_changed");
            var reader = new StreamingDirectoryReader(current, relative, actual, validate);
            current = null;
            return reader;
        }
        finally { current?.Dispose(); }
    }

    private sealed class StreamingDirectoryReader : IInventoryDirectoryReader
    {
        private readonly SafeFileHandle handle;
        private readonly string relative;
        private readonly Action validate;
        private readonly IEnumerator<InventoryChild> children;
        private readonly object gate = new();
        private bool disposed;
        private bool ended;
        private CancellationToken currentToken;
        public string DirectoryIdentity { get; }

        public StreamingDirectoryReader(SafeFileHandle handle, string relative, string identity, Action validate)
        {
            this.handle = handle;
            this.relative = relative;
            this.validate = validate;
            DirectoryIdentity = identity;
            children = EnumerateHandleChildren(handle, Check).GetEnumerator();
        }

        private void Check()
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            currentToken.ThrowIfCancellationRequested();
            validate();
            var metadata = ReadInformation(handle);
            if ((metadata.Attributes & (0x10 | 0x400)) != 0x10 || ReadNativeIdentity(handle) != DirectoryIdentity)
                throw new FixtureSafetyException("inventory.directory_identity_changed");
        }

        public Task<InventoryCapturePage> ReadPageAsync(int pageSize, CancellationToken cancellationToken)
            => Task.Run(() =>
            {
                try { return ReadPage(pageSize, cancellationToken); }
                catch (FixtureSafetyException error) { throw new InventoryIndexException(error.Code); }
                catch (UnauthorizedAccessException) { throw new InventoryIndexException("inventory.capture_access_denied"); }
                catch (IOException) { throw new InventoryIndexException("inventory.capture_io_error"); }
            }, cancellationToken);

        private InventoryCapturePage ReadPage(int pageSize, CancellationToken token)
        {
            lock (gate)
            {
                if (pageSize is < 1 or > 1000) throw new FixtureSafetyException("inventory.page_size");
                currentToken = token;
                Check();
                var page = new List<InventoryEntry>(pageSize);
                while (!ended && page.Count < pageSize)
                {
                    Check();
                    if (!children.MoveNext()) { ended = true; break; }
                    var child = children.Current;
                    var path = relative.Length == 0 ? child.Name : relative + "/" + child.Name;
                    if (path.Length > 240) throw new FixtureSafetyException("inventory.path_length");
                    var directory = (child.Attributes & 0x10) != 0;
                    var kind = directory ? InventoryEntryKind.Directory : InventoryEntryKind.File;
                    var observedIdentity = DirectoryIdentity[..16] + ":" + child.FileId;
                    if ((child.Attributes & 0x400) != 0)
                    {
                        page.Add(new(path, InventoryEntryKind.ExcludedReparse, string.Empty, 0, child.ModifiedUtc, child.Attributes));
                        continue;
                    }
                    try
                    {
                        using var childHandle = OpenInventoryChild(handle, child.Name, directory);
                        var info = ReadInformation(childHandle);
                        var id = ReadNativeIdentity(childHandle);
                        var length = directory ? 0 : checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow));
                        var modified = DateTime.FromFileTimeUtc(((long)(uint)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime);
                        var changed = id != observedIdentity || (info.Attributes & 0x400) != 0 || ((info.Attributes & 0x10) != 0) != directory ||
                            (!directory && (length != child.Length || modified != child.ModifiedUtc));
                        page.Add(new(path, kind, id, length, modified, info.Attributes, changed ? "inventory.entry_changed" : null));
                    }
                    catch (FixtureSafetyException error)
                    {
                        page.Add(new(path, kind, observedIdentity, directory ? 0 : child.Length, child.ModifiedUtc, child.Attributes, error.Code));
                    }
                }
                Check();
                return new InventoryCapturePage(page, ended);
            }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (!disposed) { disposed = true; try { children.Dispose(); } finally { handle.Dispose(); } }
            }
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>Enumerates actual child records from retained handles; never uses fixture plans.</summary>
    internal InventorySnapshot CaptureInventory(string root, CancellationToken cancellationToken, Action<string>? onObserved = null)
    {
        if (!handles.TryGetValue(root, out var retained)) throw new FixtureSafetyException("inventory.root_not_pinned");
        ValidateIdentity(retained);
        var started = Stopwatch.StartNew();
        var entries = new List<InventoryEntry>();
        var directoryCount = 0;
        var rootIdentity = ReadNativeIdentity(retained.Handle);
        Walk(retained.Handle, string.Empty, 0);
        ValidateIdentity(retained);
        return new InventorySnapshot(rootIdentity, entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray());

        void Check()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (started.Elapsed > TimeSpan.FromSeconds(15)) throw new FixtureSafetyException("inventory.duration_limit");
        }

        void Walk(SafeFileHandle directory, string prefix, int depth)
        {
            Check();
            if (depth > 8 || ++directoryCount > 64) throw new FixtureSafetyException("inventory.directory_limit");
            foreach (var child in EnumerateHandleChildren(directory, Check))
            {
                Check();
                if (prefix.Length == 0 && child.Name is ".file-organizer-fixture-owner" or "manifest.json" or "receipt.json") continue;
                var relative = prefix.Length == 0 ? child.Name : prefix + "/" + child.Name;
                if (relative.Length > 240 || entries.Count >= 256) throw new FixtureSafetyException("inventory.entry_limit");
                onObserved?.Invoke(relative);
                var directoryKind = (child.Attributes & 0x10) != 0;
                var kind = directoryKind ? InventoryEntryKind.Directory : InventoryEntryKind.File;
                var observedIdentity = rootIdentity[..16] + ":" + child.FileId;
                if ((child.Attributes & 0x400) != 0)
                {
                    entries.Add(new(relative, InventoryEntryKind.ExcludedReparse, string.Empty, 0, child.ModifiedUtc, child.Attributes));
                    continue;
                }
                SafeFileHandle opened;
                try
                {
                    opened = OpenInventoryChild(directory, child.Name, directoryKind);
                }
                catch (FixtureSafetyException error)
                {
                    entries.Add(new(relative, kind, observedIdentity, directoryKind ? 0 : child.Length, child.ModifiedUtc, child.Attributes, error.Code));
                    continue;
                }
                using (opened)
                {
                    var info = ReadInformation(opened);
                    var identity = ReadNativeIdentity(opened);
                    var length = directoryKind ? 0 : checked((long)(((ulong)info.SizeHigh << 32) | info.SizeLow));
                    var modified = DateTime.FromFileTimeUtc(((long)(uint)info.WriteTime.dwHighDateTime << 32) | (uint)info.WriteTime.dwLowDateTime);
                    // NTFS directory-entry attributes can lag fresh handle metadata (Microsoft
                    // FindFirstFile remarks). A cached directory timestamp is not a stability token:
                    // retain identity/kind/no-reparse checks and store its fresh handle timestamp.
                    // For regular files a length/time disagreement remains conservative stale
                    // observation evidence, not proof that a concurrent mutation occurred.
                    if ((info.Attributes & 0x400) != 0 || !identity.Equals(observedIdentity, StringComparison.Ordinal) ||
                        ((info.Attributes & 0x10) != 0) != directoryKind || (!directoryKind && (length != child.Length || modified != child.ModifiedUtc)))
                    {
                        if (Environment.GetEnvironmentVariable("FILE_ORGANIZER_NATIVE_DIAGNOSTICS") == "1")
                            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                            {
                                Diagnostic = "fixture-native-observation", RelativePath = relative, Directory = directoryKind,
                                EnumeratedIdentity = observedIdentity, HandleIdentity = identity,
                                EnumeratedLength = child.Length, HandleLength = length,
                                EnumeratedModifiedUtc = child.ModifiedUtc, HandleModifiedUtc = modified,
                                EnumeratedAttributes = child.Attributes, HandleAttributes = info.Attributes,
                            }));
                        entries.Add(new(relative, kind, observedIdentity, directoryKind ? 0 : child.Length, child.ModifiedUtc, child.Attributes, "inventory.entry_changed"));
                        continue;
                    }
                    var directoryIndex = entries.Count;
                    entries.Add(new(relative, kind, identity, length, modified, info.Attributes));
                    if (directoryKind)
                    {
                        try { Walk(opened, relative, depth + 1); }
                        catch (FixtureSafetyException error) when (error.Code.StartsWith("inventory.enumeration.", StringComparison.Ordinal))
                        {
                            // A scoped coverage gap must not hide healthy siblings or imply complete coverage.
                            entries[directoryIndex] = entries[directoryIndex] with { ErrorCode = error.Code };
                        }
                    }
                }
            }
        }
    }

    private static IEnumerable<InventoryChild> EnumerateHandleChildren(SafeFileHandle directory, Action check)
    {
        var restart = true;
        var buffer = new byte[16 * 1024];
        while (true)
        {
            check();
            Array.Clear(buffer);
            if (!GetFileInformationByHandleEx(directory, restart ? 20 : 19, buffer, (uint)buffer.Length))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 18) yield break;
                throw new FixtureSafetyException($"inventory.enumeration.{error}");
            }
            restart = false;
            var offset = 0;
            while (true)
            {
                check();
                if (offset < 0 || offset > buffer.Length - 88) throw new FixtureSafetyException("inventory.invalid_directory_record");
                var next = BitConverter.ToUInt32(buffer, offset);
                var nameLength = BitConverter.ToUInt32(buffer, offset + 60);
                if (nameLength == 0 || nameLength % 2 != 0 || nameLength > 510 || nameLength > buffer.Length - offset - 88)
                    throw new FixtureSafetyException("inventory.invalid_directory_name");
                var name = Encoding.Unicode.GetString(buffer, offset + 88, (int)nameLength);
                if (name is not ("." or ".."))
                {
                    ValidateLeaf(name);
                    var attributes = BitConverter.ToUInt32(buffer, offset + 56);
                    var size = BitConverter.ToInt64(buffer, offset + 40);
                    if (size < 0) throw new FixtureSafetyException("inventory.invalid_size");
                    yield return new InventoryChild(name, attributes, size,
                        DateTime.FromFileTimeUtc(BitConverter.ToInt64(buffer, offset + 24)),
                        Convert.ToHexString(buffer.AsSpan(offset + 72, 16)));
                }
                if (next == 0) break;
                if (next < 88 + nameLength || next > buffer.Length - offset) throw new FixtureSafetyException("inventory.invalid_directory_offset");
                offset = checked(offset + (int)next);
            }
        }
    }

    private static string ReadNativeIdentity(SafeFileHandle handle)
    {
        var bytes = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, bytes, (uint)bytes.Length))
            throw new FixtureSafetyException($"inventory.identity.{Marshal.GetLastWin32Error()}");
        return BitConverter.ToUInt64(bytes, 0).ToString("X16", System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(bytes.AsSpan(8, 16));
    }

    private static SafeFileHandle OpenInventoryChild(SafeFileHandle parent, string name, bool directory)
    {
        ValidateLeaf(name);
        var referenced = false;
        var nameBuffer = IntPtr.Zero;
        var unicodeBuffer = IntPtr.Zero;
        try
        {
            parent.DangerousAddRef(ref referenced);
            nameBuffer = Marshal.StringToHGlobalUni(name);
            var unicode = new UnicodeString { Length = checked((ushort)(name.Length * 2)), MaximumLength = checked((ushort)((name.Length + 1) * 2)), Buffer = nameBuffer };
            unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodeBuffer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(), RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeBuffer, Attributes = 0x40 | 0x1000,
            };
            // Read-only metadata opens share existing fixture writers, without relaxing their guards.
            var status = NtCreateFile(out var raw, directory ? DirectoryAccess : 0x00100080,
                ref attributes, out _, IntPtr.Zero, 0x80, directory ? 1u : 7u, 1,
                directory ? DirectoryOptions : OpenReparseSync | 0x40, IntPtr.Zero, 0);
            var result = new SafeFileHandle(raw, ownsHandle: true);
            if (status < 0 || result.IsInvalid)
            {
                result.Dispose();
                throw new FixtureSafetyException($"inventory.open.{unchecked((uint)status):X8}");
            }
            return result;
        }
        finally
        {
            if (unicodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeBuffer);
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            if (referenced) parent.DangerousRelease();
        }
    }

    private sealed record InventoryChild(string Name, uint Attributes, long Length, DateTime ModifiedUtc, string FileId);

    private SafeFileHandle OpenRelative(string parentPath, string name, uint access, uint disposition,
        uint options, out int status, bool allowMissing = false)
    {
        ValidateLeaf(name);
        if (!handles.TryGetValue(parentPath, out var parent))
        {
            throw new FixtureSafetyException("directory.parent_not_pinned");
        }

        ValidateIdentity(parent);
        var referenced = false;
        var nameBuffer = IntPtr.Zero;
        var unicodeBuffer = IntPtr.Zero;
        try
        {
            parent.Handle.DangerousAddRef(ref referenced);
            nameBuffer = Marshal.StringToHGlobalUni(name);
            var unicode = new UnicodeString
            {
                Length = checked((ushort)(name.Length * 2)),
                MaximumLength = checked((ushort)((name.Length + 1) * 2)),
                Buffer = nameBuffer,
            };
            unicodeBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
            Marshal.StructureToPtr(unicode, unicodeBuffer, false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.Handle.DangerousGetHandle(),
                ObjectName = unicodeBuffer,
                Attributes = 0x40 | 0x1000,
            };
            status = NtCreateFile(out var raw, access, ref attributes, out _, IntPtr.Zero,
                0x80, 1, disposition, options, IntPtr.Zero, 0);
            var result = new SafeFileHandle(raw, ownsHandle: true);
            if (status < 0)
            {
                if (allowMissing && IsMissing(status))
                {
                    return result;
                }

                result.Dispose();
                throw new FixtureSafetyException($"native.open.{unchecked((uint)status):X8}");
            }

            if (result.IsInvalid)
            {
                result.Dispose();
                throw new FixtureSafetyException("native.invalid_handle");
            }

            return result;
        }
        finally
        {
            if (unicodeBuffer != IntPtr.Zero) Marshal.FreeHGlobal(unicodeBuffer);
            if (nameBuffer != IntPtr.Zero) Marshal.FreeHGlobal(nameBuffer);
            if (referenced) parent.Handle.DangerousRelease();
        }
    }

    private void PinVolumeRoot(string root)
    {
        if (handles.ContainsKey(root)) return;
        var handle = CreateFileW("\\\\?\\" + root, DirectoryAccess, 1, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new FixtureSafetyException("directory.root_open_failed");
        }

        AddDirectory(root, handle);
    }

    private void AddDirectory(string path, SafeFileHandle handle)
    {
        try
        {
            var info = ReadInformation(handle);
            if ((info.Attributes & 0x10) == 0 || (info.Attributes & 0x400) != 0)
                throw new FixtureSafetyException("directory.reparse_or_not_directory");
            handles.Add(path, (handle, info));
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    public void Validate()
    {
        foreach (var entry in handles.Values) ValidateIdentity(entry);
    }

    private static void ValidateIdentity((SafeFileHandle Handle, FileInformation Identity) entry)
    {
        var current = ReadInformation(entry.Handle);
        if ((current.Attributes & 0x10) == 0 || (current.Attributes & 0x400) != 0 ||
            current.VolumeSerial != entry.Identity.VolumeSerial || current.FileIndexHigh != entry.Identity.FileIndexHigh ||
            current.FileIndexLow != entry.Identity.FileIndexLow)
            throw new FixtureSafetyException("directory.identity_changed");
    }

    public static void ValidateRegularFile(SafeFileHandle handle)
    {
        if ((ReadInformation(handle).Attributes & (0x10 | 0x400)) != 0)
            throw new FixtureSafetyException("file.not_regular");
    }

    private static void ValidateLeaf(string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.Length > 255 ||
            name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new FixtureSafetyException("native.invalid_leaf");
    }

    private static bool IsMissing(int status)
        => status == unchecked((int)0xC0000034) || status == unchecked((int)0xC000003A);

    private static FileInformation ReadInformation(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new FixtureSafetyException("handle.query_failed");
        return information;
    }

    public void Dispose()
    {
        List<Exception>? failures = null;
        foreach (var entry in handles.Values.Reverse())
        {
            try { entry.Handle.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
        }

        handles.Clear();
        if (failures is not null) throw new AggregateException("Directory pin release failed.", failures);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length; public ushort MaximumLength; public IntPtr Buffer; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME AccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME WriteTime;
        public uint VolumeSerial;
        public uint SizeHigh;
        public uint SizeLow;
        public uint Links;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out IntPtr fileHandle, uint desiredAccess, ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock, IntPtr allocationSize, uint fileAttributes, uint shareAccess,
        uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass, [Out] byte[] information, uint bufferSize);
}
