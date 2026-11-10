using System.Security.Cryptography;
using System.Text.Json;

namespace FileOrganizer.Application;

public enum InventoryEntryKind { File, Directory, ExcludedReparse }
public enum InventoryRunStatus { Running, Interrupted, Cancelled, Incomplete, Completed }
public sealed record InventoryEntry(string RelativePath, InventoryEntryKind Kind, string ObjectIdentity,
    long Length, DateTime ModifiedUtc, long Attributes, string? ErrorCode = null);
public sealed record InventorySnapshot(string RootIdentity, IReadOnlyList<InventoryEntry> Entries);
public sealed record InventoryIndexResult(string GenerationId, string Fingerprint, InventoryRunStatus Status,
    int CommittedEntries, int TotalEntries, bool Published)
{
    public bool IsAtomicSnapshot => false;
}
public sealed record InventoryRootState(string RootIdentity, string GenerationId, int ObjectCount, int EntryCount)
{
    public bool IsAtomicSnapshot => false;
}
public sealed record InventoryScanState(string GenerationId, string RootIdentity, string Fingerprint,
    InventoryRunStatus Status, int CommittedEntries, int TotalEntries);
public sealed record InventoryIndexState(IReadOnlyList<InventoryRootState> Roots, IReadOnlyList<InventoryScanState> Runs);

public interface IInventorySource
{
    Task<InventorySnapshot> CaptureAsync(CancellationToken cancellationToken);
}

public interface IInventoryStore
{
    Task<InventoryIndexResult> IndexAsync(InventorySnapshot snapshot, int batchSize, CancellationToken cancellationToken,
        IProgress<InventoryIndexResult>? progress = null,
        Func<CancellationToken, Task<InventorySnapshot>>? recapture = null);
    Task<InventoryIndexState> ReadStateAsync(CancellationToken cancellationToken);
    Task<InventoryIndexResult> CaptureAsync(IInventoryCaptureSource source, InventoryCaptureOptions options,
        string? resumeGenerationId, CancellationToken cancellationToken, IProgress<InventoryCaptureProgress>? progress = null);
}

public sealed class InventoryIndexer(IInventorySource source, IInventoryStore store)
{
    public Task<InventoryIndexResult> RunStreamingAsync(InventoryCaptureOptions options, string? resumeGenerationId = null,
        CancellationToken cancellationToken = default, IProgress<InventoryCaptureProgress>? progress = null)
    {
        if (source is not IInventoryCaptureSource streaming)
            throw new InventoryIndexException("inventory.capture_unsupported");
        return store.CaptureAsync(streaming, options, resumeGenerationId, cancellationToken, progress);
    }
    public async Task<InventoryIndexResult> RunAsync(int batchSize = 16, CancellationToken cancellationToken = default,
        IProgress<InventoryIndexResult>? progress = null)
    {
        var snapshot = await source.CaptureAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return await store.IndexAsync(snapshot, batchSize, cancellationToken, progress, source.CaptureAsync).ConfigureAwait(false);
    }
}

/// <summary>Copies and canonicalizes bounded observations; does not access the filesystem.</summary>
public sealed class PreparedInventorySnapshot
{
    private PreparedInventorySnapshot(string rootIdentity, InventoryEntry[] entries, string fingerprint)
    {
        RootIdentity = rootIdentity;
        Entries = Array.AsReadOnly(entries);
        Fingerprint = fingerprint;
    }

    public string RootIdentity { get; }
    public IReadOnlyList<InventoryEntry> Entries { get; }
    public string Fingerprint { get; }
    public bool HasErrors => Entries.Any(entry => entry.ErrorCode is not null);

    public static void ValidateCaptureIdentity(string identity) => ValidateIdentity(identity);

    public static void ValidateCaptureEntry(InventoryEntry entry, InventoryCaptureOptions options)
    {
        if (entry is null || !Enum.IsDefined(entry.Kind)) throw new InventoryIndexException("inventory.invalid_entry");
        ValidateRelativePath(entry.RelativePath, options.MaxDepth, 3200);
        if (entry.Length < 0 || entry.ModifiedUtc.Kind != DateTimeKind.Utc || entry.Attributes < 0 ||
            (entry.Kind == InventoryEntryKind.Directory && entry.Length != 0))
            throw new InventoryIndexException("inventory.invalid_metadata");
        if (entry.ErrorCode is not null && (entry.ErrorCode.Length is < 1 or > 80 ||
            entry.ErrorCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))))
            throw new InventoryIndexException("inventory.invalid_error_code");
        if (entry.Kind == InventoryEntryKind.ExcludedReparse)
        {
            if (entry.ObjectIdentity != string.Empty) throw new InventoryIndexException("inventory.excluded_identity");
        }
        else if (entry.ErrorCode is null || entry.ObjectIdentity != string.Empty) ValidateIdentity(entry.ObjectIdentity);
    }

    public static PreparedInventorySnapshot Create(InventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateIdentity(snapshot.RootIdentity);
        ArgumentNullException.ThrowIfNull(snapshot.Entries);
        if (snapshot.Entries.Count > 320) throw new InventoryIndexException("inventory.snapshot_limit");
        var entries = new InventoryEntry[snapshot.Entries.Count];
        for (var index = 0; index < entries.Length; index++) entries[index] = snapshot.Entries[index];
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var objects = new Dictionary<string, InventoryEntry>(StringComparer.Ordinal);
        var fileCount = 0;
        var directoryCount = 0;
        foreach (var entry in entries)
        {
            if (entry is null || !Enum.IsDefined(entry.Kind)) throw new InventoryIndexException("inventory.invalid_entry");
            ValidateRelativePath(entry.RelativePath);
            if (!paths.Add(entry.RelativePath)) throw new InventoryIndexException("inventory.path_collision");
            if (entry.Length < 0 || entry.ModifiedUtc.Kind != DateTimeKind.Utc || entry.Attributes < 0)
                throw new InventoryIndexException("inventory.invalid_metadata");
            if (entry.ErrorCode is not null && (entry.ErrorCode.Length is < 1 or > 80 ||
                entry.ErrorCode.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))))
                throw new InventoryIndexException("inventory.invalid_error_code");
            if (entry.Kind == InventoryEntryKind.Directory)
            {
                directoryCount++;
                if (entry.Length != 0) throw new InventoryIndexException("inventory.directory_length");
            }
            else fileCount++;
            if (entry.Kind == InventoryEntryKind.ExcludedReparse)
            {
                if (entry.ObjectIdentity != string.Empty) throw new InventoryIndexException("inventory.excluded_identity");
            }
            else if (entry.ErrorCode is null)
            {
                ValidateIdentity(entry.ObjectIdentity);
                if (objects.TryGetValue(entry.ObjectIdentity, out var prior) &&
                    (prior.Kind != entry.Kind || prior.Length != entry.Length || prior.ModifiedUtc != entry.ModifiedUtc || prior.Attributes != entry.Attributes))
                    throw new InventoryIndexException("inventory.identity_metadata_conflict");
                objects[entry.ObjectIdentity] = entry;
            }
            else if (entry.ObjectIdentity != string.Empty) ValidateIdentity(entry.ObjectIdentity);
        }

        if (fileCount > 256 || directoryCount > 64) throw new InventoryIndexException("inventory.snapshot_limit");
        var directories = entries.Where(entry => entry.Kind == InventoryEntryKind.Directory)
            .ToDictionary(entry => entry.RelativePath, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var slash = entry.RelativePath.LastIndexOf('/');
            if (slash >= 0 && !directories.ContainsKey(entry.RelativePath[..slash]))
                throw new InventoryIndexException("inventory.parent_missing");
        }

        Array.Sort(entries, (left, right) => StringComparer.Ordinal.Compare(left.RelativePath, right.RelativePath));
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new { FormatVersion = 1, snapshot.RootIdentity, Entries = entries });
        return new PreparedInventorySnapshot(snapshot.RootIdentity, entries, Convert.ToHexString(SHA256.HashData(canonical)));
    }

    private static void ValidateIdentity(string identity)
    {
        if (string.IsNullOrEmpty(identity) || identity.Length > 256 ||
            identity.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_' or '.')))
            throw new InventoryIndexException("inventory.invalid_identity");
    }

    private static void ValidateRelativePath(string path, int maxDepth = 16, int maxLength = 240)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > maxLength || path.Contains('\\') || path.Contains(':'))
            throw new InventoryIndexException("inventory.invalid_path");
        var segments = path.Split('/');
        if (segments.Length > maxDepth) throw new InventoryIndexException("inventory.path_depth");
        foreach (var segment in segments)
        {
            if (segment.Length is < 1 or > 100 || segment is "." or ".." || segment.EndsWith('.') ||
                segment.Any(character => char.IsControl(character) || char.IsWhiteSpace(character) || "<>\"|?*".Contains(character)))
                throw new InventoryIndexException("inventory.invalid_path");
            var stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
                throw new InventoryIndexException("inventory.invalid_path");
        }
    }
}

public sealed class InventoryIndexException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
