using System.Globalization;
using FileOrganizer.Application;

namespace FileOrganizer.Persistence;

/// <summary>Validates SQLite storage types before any predicate can coerce missing/bad values into matches.</summary>
internal readonly record struct IndexedInventoryMetadata(string Path, InventoryEntryKind Kind, long Length,
    DateTime ModifiedUtc, string? ErrorCode)
{
    internal bool IsAvailable => ErrorCode is null && Kind is InventoryEntryKind.File or InventoryEntryKind.Directory;

    internal static IndexedInventoryMetadata Read(object? rawPath, object? rawKind, object? rawLength,
        object? rawModified, object? rawError)
    {
        if (rawPath is not string path || string.IsNullOrEmpty(path) || path.Length > 3200 || path.Any(char.IsControl) ||
            rawKind is not long kind || kind is < 0 or > 2 || rawLength is not long length || length < 0 ||
            (kind == (long)InventoryEntryKind.Directory && length != 0) || rawModified is not string modifiedText ||
            !DateTime.TryParseExact(modifiedText, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var modified) ||
            modified.Kind != DateTimeKind.Utc || (rawError is not null && rawError is not DBNull && rawError is not string))
            throw new InventoryIndexException("inventory.query_invalid_metadata");
        var error = rawError as string;
        if (error is not null && (error.Length is < 1 or > 80 ||
            error.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))))
            throw new InventoryIndexException("inventory.query_invalid_metadata");
        return new(path, (InventoryEntryKind)kind, length, modified, error);
    }
}
