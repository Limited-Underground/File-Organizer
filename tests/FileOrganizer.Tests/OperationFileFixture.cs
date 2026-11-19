using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using FileOrganizer.Application;

namespace FileOrganizer.Tests;

/// <summary>
/// Bounded, private test data only. Windows observations use volume serial and 128-bit
/// file ID from the same open handle as the hash; other hosts use fixture-managed tokens.
/// Private path guards do not establish a hostile-filesystem or power-loss guarantee.
/// No cleanup API: all generated evidence is retained by TestWorkspace.
/// </summary>
internal sealed class OperationFileFixture : IOperationFileSystem
{
    private const int PayloadLength = 4096;
    private readonly Dictionary<string, string> identities = new(StringComparer.Ordinal);
    private readonly Dictionary<OperationBoundary, int> counts = [];
    private readonly byte[] payload = Enumerable.Range(0, PayloadLength).Select(i => (byte)(i % 251)).ToArray();
    private readonly object gate = new();
    private Guid? operationId;
    private string Source => Path.Combine(Root, "source.bin");
    private string Partial => Path.Combine(Root, "partial.bin");
    private string Destination => Path.Combine(Root, "destination.bin");
    private string Retained => Path.Combine(Root, "retained-source.bin");

    public OperationFileFixture()
    {
        Root = new TestWorkspace().Root;
        Create(Source, payload);
    }

    private OperationFileFixture(ProcessInventorySession session, bool create)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        session.ValidateProfileBoundary();
        Root = Path.Combine(session.Root, "operation-files");
        GuardPaths();
        if (create)
        {
            if (Directory.Exists(Root) || File.Exists(Root)) throw new IOException("Fixture already exists.");
            Directory.CreateDirectory(Root);
            Create(Source, payload);
        }
        else if (!Directory.Exists(Root)) throw new IOException("Fixture is missing.");
        GuardPaths();
    }

    // The session has already validated its canonical nonce and checkout-local scope.
    // No child route or fixture factory accepts an arbitrary filesystem path.
    internal static OperationFileFixture CreateSession(ProcessInventorySession session) => new(session, true);
    internal static OperationFileFixture OpenSession(ProcessInventorySession session) => new(session, false);

    public string Root { get; }
    public Action<OperationBoundary, bool>? Fault { get; set; }
    public Action<bool>? BeforeFlush { get; set; }

    public OperationPlan Plan(OperationMode mode) => new("source", "destination", mode, Observe().Source!);

    public OperationSnapshot Observe()
    {
        lock (gate)
        {
            GuardPaths();
            return new(Read(Source), Read(Partial), Read(Destination), Read(Retained));
        }
    }

    public Task<OperationSnapshot> ObserveAsync(Guid id, OperationPlan plan, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (gate)
        {
            Bind(id, plan);
            return Task.FromResult(Observe());
        }
    }

    public Task<OperationSnapshot> ApplyAsync(Guid id, OperationPlan plan, OperationBoundary boundary,
        OperationSnapshot observedBefore, CancellationToken token)
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            Bind(id, plan);
            Fault?.Invoke(boundary, false);
            token.ThrowIfCancellationRequested();
            if (Observe() != observedBefore || observedBefore.Source != plan.ExpectedSource)
                throw new OperationConflictException();

            switch (boundary)
            {
                case OperationBoundary.CreatePartial:
                    if (observedBefore.Partial is not null || observedBefore.Destination is not null || observedBefore.RetainedSource is not null)
                        throw new OperationConflictException();
                    Create(Partial, []);
                    break;
                case OperationBoundary.WriteAndFlush:
                    if (observedBefore.Partial is null || observedBefore.Destination is not null || observedBefore.RetainedSource is not null)
                        throw new OperationConflictException();
                    if (observedBefore.Partial.Length != 0 &&
                        (observedBefore.Partial.Length != plan.ExpectedSource.Length || observedBefore.Partial.Sha256 != plan.ExpectedSource.Sha256))
                        throw new OperationConflictException();
                    using (var input = new FileStream(Source, FileMode.Open, FileAccess.Read, FileShare.None))
                    using (var output = new FileStream(Partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        if (output.Length == 0)
                        {
                            Span<byte> firstHalf = stackalloc byte[PayloadLength / 2];
                            input.ReadExactly(firstHalf);
                            output.Write(firstHalf);
                            BeforeFlush?.Invoke(false);
                            input.CopyTo(output);
                            BeforeFlush?.Invoke(true);
                        }
                        output.Flush(flushToDisk: true);
                    }
                    break;
                case OperationBoundary.Promote:
                    if (observedBefore.Partial is null || observedBefore.Partial.Length != plan.ExpectedSource.Length ||
                        observedBefore.Partial.Sha256 != plan.ExpectedSource.Sha256 || observedBefore.Destination is not null || observedBefore.RetainedSource is not null)
                        throw new OperationConflictException();
                    Move(Partial, Destination);
                    break;
                case OperationBoundary.RetainSource:
                    if (plan.Mode != OperationMode.Move || observedBefore.Partial is not null || observedBefore.RetainedSource is not null ||
                        observedBefore.Destination is null || observedBefore.Destination.Length != plan.ExpectedSource.Length ||
                        observedBefore.Destination.Sha256 != plan.ExpectedSource.Sha256)
                        throw new OperationConflictException();
                    Move(Source, Retained);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(boundary));
            }
            counts[boundary] = ApplyCount(boundary) + 1;
            Fault?.Invoke(boundary, true);
            return Task.FromResult(Observe());
        }
    }

    public int ApplyCount(OperationBoundary boundary)
    {
        lock (gate) return counts.GetValueOrDefault(boundary);
    }

    public void ChangeSource() => Change(Source);
    public void ChangeDestination() => Change(Destination);

    private void Change(string path)
    {
        lock (gate)
        {
            GuardPaths();
            // Explicit fault injection may replace only this fixture's fixed owned name.
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            stream.SetLength(0);
            stream.Write("foreign fixture content"u8);
            stream.Flush(flushToDisk: true);
            identities[path] = Guid.NewGuid().ToString("N");
        }
    }

    private void Bind(Guid id, OperationPlan plan)
    {
        if (id == Guid.Empty || plan.SourceKey != "source" || plan.DestinationKey != "destination" ||
            plan.ExpectedSource.Length != PayloadLength || !Enum.IsDefined(plan.Mode))
            throw new InvalidOperationException("Only this generated fixture's bounded plan is accepted.");
        if (operationId is not null && operationId != id)
            throw new InvalidOperationException("A fixture owns one operation.");
        operationId = id;
    }

    private void Create(string path, ReadOnlySpan<byte> bytes)
    {
        GuardPaths();
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        identities.Add(path, Guid.NewGuid().ToString("N"));
    }

    private void Move(string from, string to)
    {
        File.Move(from, to, overwrite: false);
        if (!OperatingSystem.IsWindows())
        {
            identities.Add(to, identities[from]);
            identities.Remove(from);
        }
    }

    private OperationFile? Read(string path)
    {
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new IOException("Expected an ordinary fixture file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        if (stream.Length > PayloadLength) throw new IOException("Fixture size bound exceeded.");
        var identity = OperatingSystem.IsWindows() ? ReadNativeIdentity(stream.SafeFileHandle)
            : identities.TryGetValue(path, out var mapped) ? mapped : throw new IOException("Unowned file in fixture.");
        return new(identity, stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string ReadNativeIdentity(SafeFileHandle handle)
    {
        var bytes = new byte[24];
        if (!GetFileInformationByHandleEx(handle, 18, bytes, (uint)bytes.Length))
            throw new IOException($"Fixture identity failed: {Marshal.GetLastWin32Error()}");
        return BitConverter.ToUInt64(bytes, 0).ToString("X16", System.Globalization.CultureInfo.InvariantCulture)
            + ":" + Convert.ToHexString(bytes.AsSpan(8, 16));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        [Out] byte[] information, uint bufferSize);

    private void GuardPaths()
    {
        for (var current = new DirectoryInfo(Root); current is not null; current = current.Parent)
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Fixture path traverses a reparse point.");
        foreach (var path in new[] { Source, Partial, Destination, Retained })
            if ((File.Exists(path) || Directory.Exists(path)) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Fixture child is a reparse point.");
    }
}
