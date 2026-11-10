namespace FileOrganizer.Application;

/// <summary>Reads existing indexed observations only; it never scans or starts indexing.</summary>
public interface IIndexedInventoryReader
{
    Task<IndexedCatalogPage> ReadCatalogsAsync(IndexedCatalogRequest request, CancellationToken cancellationToken);
    Task<IndexedInventoryPage> ReadPageAsync(IndexedInventoryRequest request, CancellationToken cancellationToken);
}

public sealed record IndexedCatalogRequest(int PageSize = 50, string? AfterRootIdentity = null);
public sealed record IndexedCatalog(string RootIdentity, string? GenerationId, int EntryCount, bool HasRetainedUnpublishedAttempts)
{
    public bool HasPublishedGeneration => GenerationId is not null;
    public bool IsAtomicSnapshot => false;
    public bool LiveAvailabilityKnown => false;
}
public sealed record IndexedCatalogPage(IReadOnlyList<IndexedCatalog> Catalogs, string? NextAfterRootIdentity);
public enum IndexedInventorySearchScope { Name, Path }
public enum IndexedInventoryCaseMode { IgnoreCase, MatchCase }
public enum IndexedInventoryMetadataMode { Any, Available, Unavailable }
public sealed record IndexedInventoryFilter(string Text = "", IndexedInventorySearchScope Scope = IndexedInventorySearchScope.Name,
    InventoryEntryKind? Kind = null, long? MinBytes = null, long? MaxBytes = null,
    DateOnly? ModifiedFrom = null, DateOnly? ModifiedThrough = null,
    IndexedInventoryCaseMode CaseMode = IndexedInventoryCaseMode.IgnoreCase,
    IndexedInventoryMetadataMode Metadata = IndexedInventoryMetadataMode.Any)
{
    public bool IsActive => !string.IsNullOrEmpty(Text) || Kind is not null || MinBytes is not null || MaxBytes is not null ||
        ModifiedFrom is not null || ModifiedThrough is not null || Metadata != IndexedInventoryMetadataMode.Any;

    public static IndexedInventoryFilter Normalize(IndexedInventoryFilter? filter)
    {
        filter ??= new IndexedInventoryFilter();
        if (filter.Text is null || filter.Text.Length > 128 || filter.Text.Any(char.IsControl) ||
            !Enum.IsDefined(filter.Scope) || (filter.Kind is { } kind && !Enum.IsDefined(kind)) ||
            !Enum.IsDefined(filter.CaseMode) || !Enum.IsDefined(filter.Metadata) || filter.MinBytes < 0 || filter.MaxBytes < 0 ||
            filter.MinBytes > filter.MaxBytes || filter.ModifiedFrom > filter.ModifiedThrough)
            throw new InventoryIndexException("inventory.query_invalid_filter");
        // Explicit ordinal comparison is a search policy. Source case capabilities are not stored.
        // Preserve literal whitespace, case and code points; no trim or Unicode normalization.
        return filter;
    }
}
public sealed record IndexedInventoryRequest(string RootIdentity, string GenerationId, int PageSize = 50,
    IndexedInventoryCursor? After = null, IndexedInventoryFilter? Filter = null);
public sealed record IndexedInventoryCursor(string RootIdentity, string GenerationId, int PageSize, string AfterRelativePath,
    IndexedInventoryFilter? Filter = null);
public sealed record IndexedInventoryRow(string RelativePath, InventoryEntryKind Kind, long Length, DateTime ModifiedUtc, string? ErrorCode)
{
    public string Name => RelativePath[(RelativePath.LastIndexOf('/') + 1)..];
    public bool HasUsableMetadata => ErrorCode is null && Kind is InventoryEntryKind.File or InventoryEntryKind.Directory;
    public bool HasSize => HasUsableMetadata && Kind == InventoryEntryKind.File;
}
public sealed record IndexedInventoryPage(IndexedCatalog Catalog, IReadOnlyList<IndexedInventoryRow> Rows,
    IndexedInventoryCursor? NextCursor, bool IsCurrentPublication, string? CurrentPublishedGenerationId);
