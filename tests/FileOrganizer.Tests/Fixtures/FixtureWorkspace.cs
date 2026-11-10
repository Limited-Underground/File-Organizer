using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FileOrganizer.Application;

namespace FileOrganizer.Tests.Fixtures;

internal sealed class FixtureWorkspace : IDisposable
{
    private readonly WindowsDirectoryPins pins = new();
    private readonly Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileStream> files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly object gate = new();
    private FileStream? marker;
    private byte[] markerBytes = [];
    private long reservedBytes;
    private long writtenBytes;
    private int payloadCount;
    private int generationStarted;
    private bool disposed;

    private FixtureWorkspace(FixtureLimits limits)
    {
        Limits = limits;
        RunId = Guid.NewGuid().ToString("N");
        Root = string.Empty;
    }

    public string Root { get; private set; }
    public string RunId { get; }
    public FixtureLimits Limits { get; }
    public long WrittenBytes => Interlocked.Read(ref writtenBytes);

    public void BeginGeneration()
    {
        if (Interlocked.Exchange(ref generationStarted, 1) != 0)
        {
            throw new FixtureSafetyException("generation.already_started");
        }

        CheckBudget(CancellationToken.None);
    }

    public static FixtureWorkspace Create(FixtureLimits? limits = null, string? evidenceTask = null)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new FixtureSafetyException("platform.windows_required");
        }

        var workspace = new FixtureWorkspace(limits ?? new FixtureLimits());
        try
        {
            var evidence = evidenceTask ?? Environment.GetEnvironmentVariable("FILE_ORGANIZER_EVIDENCE_TASK") ?? "FO-003-fixtures-20261029";
            if (evidence is not ("FO-003-fixtures-20261029" or "FO-005-indexing-20261029" or "FO-005-recovery-20261029" or "FO-005-streaming-20261029" or "FO-012-indexed-ui-20261029" or "FO-012-search-20261029" or "FO-012-filters-20261101"))
                throw new FixtureSafetyException("fixture.invalid_evidence_task");
            workspace.Limits.Validate();
            var checkout = FindCheckout();
            workspace.pins.PinAncestors(checkout);
            var parent = checkout;
            foreach (var segment in new[] { "artifacts", evidence, "fixtures" })
            {
                workspace.pins.CreateChildDirectory(parent, segment, allowExisting: true);
                parent = Path.Combine(parent, segment);
            }

            workspace.CheckResources(CancellationToken.None);
            workspace.Root = Path.Combine(parent, workspace.RunId);
            workspace.pins.CreateChildDirectory(parent, workspace.RunId, allowExisting: false);
            workspace.markerBytes = Encoding.UTF8.GetBytes($"File Organizer disposable fixture v1\n{workspace.RunId}\n");
            workspace.marker = workspace.OpenNew(".file-organizer-fixture-owner");
            workspace.marker.Write(workspace.markerBytes);
            workspace.marker.Flush(true);
            workspace.ValidateOwnership();
            return workspace;
        }
        catch (Exception original)
        {
            try { workspace.Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Fixture creation and handle release failed.", original, cleanup); }
            throw;
        }
    }

    public string ResolveOwnedPath(string relative)
    {
        lock (gate)
        {
            ValidateOwnership();
            var segments = ValidateRelative(relative);
            var fullPath = Path.GetFullPath(Path.Combine(Root, Path.Combine(segments)));
            if (!fullPath.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new FixtureSafetyException("path.outside_root");
            }

            var prefix = string.Empty;
            var parent = Root;
            for (var index = 0; index < segments.Length; index++)
            {
                var segment = segments[index];
                prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
                if (names.TryGetValue(prefix, out var prior) && !StringComparer.Ordinal.Equals(prior, prefix))
                {
                    throw new FixtureSafetyException("path.case_collision");
                }

                if (directories.Contains(prefix))
                {
                    parent = Path.Combine(parent, segment);
                    continue;
                }

                if (files.TryGetValue(prefix, out var ownedFile))
                {
                    WindowsDirectoryPins.ValidateRegularFile(ownedFile.SafeFileHandle);
                    if (index < segments.Length - 1)
                    {
                        throw new FixtureSafetyException("path.parent_is_file");
                    }

                    continue;
                }

                if (pins.ChildExists(parent, segment))
                    throw new FixtureSafetyException("path.unowned_existing");

                // The remaining descendants cannot exist beneath a missing parent. Each eventual
                // creation still uses a single leaf relative to its newly retained parent handle.
                break;
            }

            return fullPath;
        }
    }

    public void EnsureDirectory(string relative)
    {
        lock (gate)
        {
            CheckBudget(CancellationToken.None);
            var segments = ValidateRelative(relative);
            RejectControlName(segments[0]);
            var prefix = string.Empty;
            foreach (var segment in segments)
            {
                prefix = prefix.Length == 0 ? segment : prefix + "/" + segment;
                var path = ResolveOwnedPath(prefix);
                if (directories.Contains(prefix))
                {
                    continue;
                }

                if (names.ContainsKey(prefix) || directories.Count >= Limits.MaxDirectories)
                {
                    throw new FixtureSafetyException("quota.directories_or_path_conflict");
                }

                pins.CreateChildDirectory(Path.GetDirectoryName(path)!, segment, allowExisting: false);
                directories.Add(prefix);
                names.Add(prefix, prefix);
            }
        }
    }

    public FixtureFileWriter CreatePayload(string relative, long expectedLength)
    {
        lock (gate)
        {
            CheckBudget(CancellationToken.None);
            RejectControlName(ValidateRelative(relative)[0]);
            var path = ResolveOwnedPath(relative);
            if (expectedLength < 0 || expectedLength > Limits.MaxFileBytes || payloadCount >= Limits.MaxFiles ||
                expectedLength > Limits.MaxTotalBytes - reservedBytes || names.ContainsKey(relative))
            {
                throw new FixtureSafetyException("quota.payload");
            }

            var slash = relative.LastIndexOf('/');
            if (slash >= 0)
            {
                EnsureDirectory(relative[..slash]);
            }

            var stream = OpenNewPath(path);
            names.Add(relative, relative);
            files.Add(relative, stream);
            reservedBytes = checked(reservedBytes + expectedLength);
            payloadCount++;
            return new FixtureFileWriter(this, stream, expectedLength);
        }
    }

    public void WriteControl(string name, ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            // A bounded failure receipt remains writable after cancellation or the time budget expires.
            ValidateOwnership();
            if (name != "receipt.json")
            {
                CheckResources(CancellationToken.None);
            }
            if (name is not ("manifest.json" or "receipt.json") || bytes.Length > 1024 * 1024)
            {
                throw new FixtureSafetyException("control.invalid");
            }

            if (!files.TryGetValue(name, out var stream))
            {
                stream = OpenNew(name);
                files.Add(name, stream);
                names.Add(name, name);
            }
            else if (name != "receipt.json")
            {
                throw new FixtureSafetyException("control.already_written");
            }

            WindowsDirectoryPins.ValidateRegularFile(stream.SafeFileHandle);
            stream.Position = 0;
            stream.Write(bytes);
            stream.SetLength(bytes.Length);
            stream.Flush(true);
        }
    }

    public byte[] ReadOwnedBytes(string relative)
    {
        lock (gate)
        {
            ValidateOwnership();
            _ = ResolveOwnedPath(relative);
            if (!files.TryGetValue(relative, out var stream))
            {
                throw new FixtureSafetyException("file.not_owned");
            }

            WindowsDirectoryPins.ValidateRegularFile(stream.SafeFileHandle);
            if (stream.Length > Limits.MaxFileBytes && relative is not ("manifest.json" or "receipt.json"))
            {
                throw new FixtureSafetyException("quota.read");
            }

            var position = stream.Position;
            try
            {
                stream.Flush();
                stream.Position = 0;
                var bytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(bytes);
                return bytes;
            }
            finally
            {
                stream.Position = position;
            }
        }
    }

    public byte[] ReadControlBytes(string name)
    {
        if (name is not ("manifest.json" or "receipt.json"))
        {
            throw new FixtureSafetyException("control.invalid");
        }

        return ReadOwnedBytes(name);
    }

    public void CheckBudget(CancellationToken cancellationToken)
    {
        lock (gate)
        {
            ValidateOwnership();
            CheckResources(cancellationToken);
        }
    }

    internal InventorySnapshot CaptureInventory(CancellationToken cancellationToken, Action<string>? onObserved = null)
    {
        lock (gate)
        {
            ValidateOwnership();
            return pins.CaptureInventory(Root, cancellationToken, onObserved);
        }
    }

    public void ValidateOwnership()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            pins.Validate();
            if (marker is null)
            {
                throw new FixtureSafetyException("ownership.missing");
            }

            WindowsDirectoryPins.ValidateRegularFile(marker.SafeFileHandle);
            if (marker.Length != markerBytes.Length)
            {
                throw new FixtureSafetyException("ownership.marker_changed");
            }

            marker.Position = 0;
            var actual = new byte[markerBytes.Length];
            marker.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(markerBytes))
            {
                throw new FixtureSafetyException("ownership.marker_changed");
            }
        }
    }

    private void CheckResources(CancellationToken token, long pendingBytes = 0)
    {
        token.ThrowIfCancellationRequested();
        if (elapsed.Elapsed > Limits.MaxDuration)
        {
            throw new FixtureSafetyException("quota.duration");
        }

        var drive = new DriveInfo(Path.GetPathRoot(Root.Length == 0 ? FindCheckout() : Root)!);
        var available = drive.AvailableFreeSpace;
        if (available < Limits.MinimumFreeBytes || pendingBytes > available - Limits.MinimumFreeBytes)
        {
            throw new FixtureSafetyException("quota.free_space");
        }
    }

    private string[] ValidateRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains('\\'))
        {
            throw new FixtureSafetyException("path.invalid");
        }

        var segments = relative.Split('/');
        if (segments.Length > Limits.MaxDepth || relative.Length > 240)
        {
            throw new FixtureSafetyException("path.depth_or_length");
        }

        foreach (var segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Length > 100 ||
                segment.EndsWith('.') || char.IsWhiteSpace(segment[^1]) || char.IsWhiteSpace(segment[0]) ||
                segment.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "<>:\"|?*".Contains(c)))
            {
                throw new FixtureSafetyException("path.invalid_segment");
            }

            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                "123456789¹²³".Contains(stem[3])))
            {
                throw new FixtureSafetyException("path.reserved_name");
            }
        }

        return segments;
    }

    private static void RejectControlName(string firstSegment)
    {
        if (firstSegment.Equals("manifest.json", StringComparison.OrdinalIgnoreCase) ||
            firstSegment.Equals("receipt.json", StringComparison.OrdinalIgnoreCase) ||
            firstSegment.Equals(".file-organizer-fixture-owner", StringComparison.OrdinalIgnoreCase))
        {
            throw new FixtureSafetyException("path.reserved_control");
        }
    }

    private FileStream OpenNew(string name) => OpenNewPath(Path.Combine(Root, name));

    private FileStream OpenNewPath(string path)
    {
        var handle = pins.OpenNewFile(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        try
        {
            return new FileStream(handle, FileAccess.ReadWrite, bufferSize: 4096, isAsync: false);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static string FindCheckout()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "FileOrganizer.slnx")) &&
                File.Exists(Path.Combine(current.FullName, "tests", "FileOrganizer.Tests", "FileOrganizer.Tests.csproj")))
            {
                return current.FullName;
            }
        }

        throw new FixtureSafetyException("checkout.not_found");
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            List<Exception>? failures = null;
            foreach (var stream in files.Values)
            {
                try { stream.Dispose(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }

            try { marker?.Dispose(); }
            catch (Exception exception) { (failures ??= []).Add(exception); }
            finally
            {
                try { pins.Dispose(); }
                catch (Exception exception) { (failures ??= []).Add(exception); }
            }

            if (failures is not null)
                throw new AggregateException("Fixture handle release failed.", failures);
        }
    }

    internal sealed class FixtureFileWriter(FixtureWorkspace workspace, FileStream stream, long expectedLength)
    {
        private long written;
        private bool completed;
        public long WrittenBytes => written;

        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            lock (workspace.gate)
            {
                workspace.CheckBudget(cancellationToken);
                if (completed || bytes.Length > expectedLength - written || bytes.Length > workspace.Limits.MaxTotalBytes - workspace.writtenBytes)
                {
                    throw new FixtureSafetyException("quota.write");
                }

                WindowsDirectoryPins.ValidateRegularFile(stream.SafeFileHandle);
                workspace.CheckResources(cancellationToken, bytes.Length);
                var previousPosition = stream.Position;
                try
                {
                    stream.Write(bytes.Span);
                }
                finally
                {
                    // Even an exceptional write can leave a prefix; the receipt must count actual bytes.
                    var actual = stream.Position - previousPosition;
                    written = checked(written + actual);
                    workspace.writtenBytes = checked(workspace.writtenBytes + actual);
                }

                return Task.CompletedTask;
            }
        }

        public Task<string> CompleteAsync(DateTime modifiedUtc, CancellationToken cancellationToken)
        {
            lock (workspace.gate)
            {
                workspace.CheckBudget(cancellationToken);
                if (completed || written != expectedLength || modifiedUtc.Kind != DateTimeKind.Utc)
                {
                    throw new FixtureSafetyException("file.incomplete_or_timestamp");
                }

                stream.Flush(true);
                File.SetLastWriteTimeUtc(stream.SafeFileHandle, modifiedUtc);
                stream.Position = 0;
                var digest = Convert.ToHexString(SHA256.HashData(stream));
                stream.Position = written;
                completed = true;
                return Task.FromResult(digest);
            }
        }
    }
}
