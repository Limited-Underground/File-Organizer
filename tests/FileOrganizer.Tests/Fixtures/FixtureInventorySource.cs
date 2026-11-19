using FileOrganizer.Application;

namespace FileOrganizer.Tests.Fixtures;

/// <summary>Read-only pilot source: only a live internally minted fixture can supply its root.</summary>
internal sealed class FixtureInventorySource(FixtureWorkspace workspace, Action<string>? onObserved = null) : IInventorySource
{
    public Task<InventorySnapshot> CaptureAsync(CancellationToken cancellationToken) =>
        Task.Run(() => workspace.CaptureInventory(cancellationToken, onObserved), cancellationToken);
}
