using System.Diagnostics;
using System.Text.Json;
using FileOrganizer.Application;

namespace FileOrganizer.Tests.Fixtures;

/// <summary>Native streaming capture restricted to the fixed generated streaming-fixture catalogue.</summary>
internal sealed class FixtureStreamingSource : IInventoryCaptureSource
{
    private readonly string id;
    private readonly string expectedIdentity;
    private readonly StreamingFixture? owner;

    public FixtureStreamingSource(StreamingFixture fixture)
    { owner = fixture; id = fixture.Id; expectedIdentity = fixture.RootIdentity; }

    private FixtureStreamingSource(string id, string expectedIdentity)
    { ValidateId(id); this.id = id; this.expectedIdentity = expectedIdentity; }

    public static FixtureStreamingSource FromId(string id, string expectedRootIdentity) => new(id, expectedRootIdentity);

    internal static void ValidateId(string id)
    {
        if (id.Length != 32 || !Guid.TryParseExact(id, "N", out _) || id.Any(value => !(value is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new FixtureSafetyException("streaming.invalid_fixture_id");
    }

    public Task<IInventoryCaptureLease> OpenReadLeaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            owner?.Validate();
            return Task.FromResult<IInventoryCaptureLease>(new ReadLease(id, expectedIdentity));
        }
        catch (FixtureSafetyException error) { throw new InventoryIndexException(error.Code); }
        catch (UnauthorizedAccessException) { throw new InventoryIndexException("inventory.capture_access_denied"); }
        catch (IOException) { throw new InventoryIndexException("inventory.capture_io_error"); }
        catch (JsonException) { throw new InventoryIndexException("inventory.capture_invalid_marker"); }
    }

    // Compatibility route retains legacy small-snapshot limits. Large runs must use streaming ports.
    public async Task<InventorySnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        await using var lease = await OpenReadLeaseAsync(cancellationToken);
        var pending = new Queue<(string Path, string Identity)>();
        pending.Enqueue((string.Empty, lease.RootIdentity));
        var entries = new List<InventoryEntry>();
        var directoryCount = 0;
        while (pending.TryDequeue(out var directory))
        {
            if (++directoryCount > 64) throw new FixtureSafetyException("inventory.directory_limit");
            await using var reader = await lease.OpenDirectoryAsync(directory.Path, directory.Identity, cancellationToken);
            while (true)
            {
                var page = await reader.ReadPageAsync(64, cancellationToken);
                foreach (var entry in page.Entries)
                {
                    if (entries.Count >= 256) throw new FixtureSafetyException("inventory.entry_limit");
                    entries.Add(entry);
                    if (entry.Kind == InventoryEntryKind.Directory && entry.ErrorCode is null)
                        pending.Enqueue((entry.RelativePath, entry.ObjectIdentity));
                }
                if (page.EndOfDirectory) break;
            }
        }
        return new InventorySnapshot(lease.RootIdentity, entries.OrderBy(entry => entry.RelativePath, StringComparer.Ordinal).ToArray());
    }

    private sealed class ReadLease : IInventoryCaptureLease
    {
        private readonly WindowsDirectoryPins pins = new();
        private readonly string root;
        private readonly FileStream marker;
        private readonly byte[] markerBytes;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        private readonly object gate = new();
        private bool disposed;
        public string RootIdentity { get; }

        public ReadLease(string id, string expected)
        {
            ValidateId(id);
            FileStream? openedMarker = null;
            try
            {
                var parent = StreamingFixture.FixedParent(pins, create: false);
                var session = Path.Combine(parent, id);
                pins.PinAncestors(session);
                openedMarker = new FileStream(pins.OpenReadFile(session, ".streaming-fixture-owner"), FileAccess.Read);
                if (openedMarker.Length is < 1 or > 4096) throw new FixtureSafetyException("streaming.invalid_marker");
                markerBytes = new byte[(int)openedMarker.Length];
                openedMarker.ReadExactly(markerBytes);
                var ownership = JsonSerializer.Deserialize<StreamingFixtureOwnership>(markerBytes);
                if (ownership is null || ownership.FormatVersion != 1 || ownership.Id != id || ownership.RootIdentity != expected)
                    throw new FixtureSafetyException("streaming.marker_identity_mismatch");
                root = Path.Combine(session, "payload");
                pins.PinAncestors(root);
                RootIdentity = pins.GetPinnedIdentity(root);
                if (RootIdentity != expected) throw new FixtureSafetyException("streaming.root_identity_mismatch");
                marker = openedMarker;
            }
            catch { openedMarker?.Dispose(); pins.Dispose(); throw; }
        }

        private void Validate()
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (elapsed.Elapsed > TimeSpan.FromMinutes(45)) throw new FixtureSafetyException("streaming.capture_deadline");
                pins.Validate();
                if (pins.GetPinnedIdentity(root) != RootIdentity || marker.Length != markerBytes.Length)
                    throw new FixtureSafetyException("streaming.ownership_changed");
                marker.Position = 0;
                var actual = new byte[markerBytes.Length];
                marker.ReadExactly(actual);
                if (!actual.AsSpan().SequenceEqual(markerBytes)) throw new FixtureSafetyException("streaming.marker_changed");
            }
        }

        public Task<IInventoryDirectoryReader> OpenDirectoryAsync(string relativePath, string expectedIdentity, CancellationToken cancellationToken)
        {
            try
            {
                lock (gate)
                {
                    Validate();
                    return Task.FromResult(pins.OpenStreamingReader(root, relativePath, expectedIdentity, Validate, cancellationToken));
                }
            }
            catch (FixtureSafetyException error) { throw new InventoryIndexException(error.Code); }
            catch (UnauthorizedAccessException) { throw new InventoryIndexException("inventory.capture_access_denied"); }
            catch (IOException) { throw new InventoryIndexException("inventory.capture_io_error"); }
        }

        public ValueTask DisposeAsync()
        {
            lock (gate)
            {
                if (!disposed) { disposed = true; try { marker.Dispose(); } finally { pins.Dispose(); } }
            }
            return ValueTask.CompletedTask;
        }
    }
}
