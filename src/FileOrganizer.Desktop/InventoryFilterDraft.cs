using System.Globalization;
using FileOrganizer.Application;

namespace FileOrganizer.Desktop;

/// <summary>Uncommitted editor values; parsing happens only after query admission.</summary>
internal sealed record InventoryFilterDraft(
    string Text = "", IndexedInventorySearchScope Scope = IndexedInventorySearchScope.Name,
    InventoryEntryKind? Kind = null, string MinBytes = "", string MaxBytes = "",
    string ModifiedFrom = "", string ModifiedThrough = "",
    IndexedInventoryCaseMode CaseMode = IndexedInventoryCaseMode.IgnoreCase,
    IndexedInventoryMetadataMode Metadata = IndexedInventoryMetadataMode.Any)
{
    public IndexedInventoryFilter Parse() => IndexedInventoryFilter.Normalize(new(Text, Scope, Kind,
        Bytes(MinBytes), Bytes(MaxBytes), Date(ModifiedFrom), Date(ModifiedThrough), CaseMode, Metadata));

    private static long? Bytes(string value)
    {
        if (value == "") return null;
        if (value is null || !value.All(char.IsAsciiDigit) ||
            !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes))
            throw new InventoryIndexException("inventory.query_invalid_filter");
        return bytes;
    }

    private static DateOnly? Date(string value)
    {
        if (value == "") return null;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new InventoryIndexException("inventory.query_invalid_filter");
        return date;
    }
}
