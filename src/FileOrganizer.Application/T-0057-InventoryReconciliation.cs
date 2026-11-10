// T-0057 / FO-006 item 2. Supplied fixture truth; no real source adapter or freshness claim.
namespace FileOrganizer.Application;

public enum SuppliedInventoryCoverage { Complete, Unknown, Offline }
public sealed record InventoryReconciliationPlan(string OperationId, InventoryCoverageReceipt Before,
    string JournalIdentity, long NextUsn, SuppliedInventoryCoverage Coverage,
    InventoryIdentityEvidence IdentityEvidence = InventoryIdentityEvidence.Unverified);
public sealed record InventoryReconciliationState(string OperationId, string RequestHash,
    InventoryRootState Root, InventoryScanState Scan, InventoryCoverageReceipt Receipt)
{
    public InventoryCoverageFloor Floor => new(Root.RootIdentity, Root.GenerationId, Scan.Fingerprint,
        Receipt.LastNoticeSequence, Receipt.Issues);
    public bool IsAtomicSnapshot => false;
}
public interface IInventoryReconciliationStore
{
    Task<InventoryReconciliationState> ReconcileAsync(InventorySnapshot snapshot, InventoryReconciliationPlan plan,
        int batchSize, CancellationToken cancellationToken, Func<CancellationToken, Task<InventorySnapshot>> recapture);
    Task<InventoryReconciliationState?> ReadReconciliationAsync(string rootIdentity, CancellationToken cancellationToken);
}

/// <summary>Reuses the accepted capture and recapture barrier; supplied coverage is not native evidence.</summary>
public sealed class InventoryReconciler(IInventorySource source, IInventoryReconciliationStore store)
{
    public async Task<InventoryReconciliationState> RunAsync(InventoryReconciliationPlan plan, int batchSize = 16,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Coverage != SuppliedInventoryCoverage.Complete)
            throw new InventoryIndexException("inventory.reconciliation_unverified_coverage");
        var snapshot = await source.CaptureAsync(cancellationToken).ConfigureAwait(false);
        return await store.ReconcileAsync(snapshot, plan, batchSize, cancellationToken, source.CaptureAsync).ConfigureAwait(false);
    }
}
