// T-0067: supplied stable identity is scoped to a managed journal, not native-source acceptance.
namespace FileOrganizer.Application;

public enum InventoryIdentityEvidence { Unverified, SuppliedStableWithinJournal }
public enum InventoryDifferenceKind { Added, Removed, Renamed, Changed, Unknown }
public sealed record InventoryComparisonRequest(string RootIdentity, string BeforeGeneration, string AfterGeneration,
    SuppliedInventoryCoverage BeforeCoverage = SuppliedInventoryCoverage.Complete,
    SuppliedInventoryCoverage AfterCoverage = SuppliedInventoryCoverage.Complete);
public sealed record InventoryDifference(InventoryDifferenceKind Kind, string? BeforePath, string? AfterPath,
    bool MetadataChanged, string Reason);
public sealed record InventoryComparisonResult(string RootIdentity, string JournalIdentity,
    string BeforeGeneration, string AfterGeneration, IReadOnlyList<InventoryDifference> Differences)
{
    public bool IsAtomicSnapshot => false;
}
public interface IInventoryGenerationComparer
{
    Task<InventoryComparisonResult> CompareAsync(InventoryComparisonRequest request, CancellationToken cancellationToken = default);
}
