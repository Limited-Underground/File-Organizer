namespace FileOrganizer.Application;

public sealed record StoreState(int SchemaVersion, int StartupCount);

public interface IApplicationStore
{
    Task<StoreState> InitializeAsync(CancellationToken cancellationToken);
}
