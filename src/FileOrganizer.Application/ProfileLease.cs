namespace FileOrganizer.Application;

/// <summary>Holds exclusive ownership for a profile until the desktop host has stopped.</summary>
public sealed class ProfileLease : IDisposable
{
    private FileStream? handle;

    private ProfileLease(FileStream handle)
    {
        this.handle = handle;
    }

    public static ProfileLease Acquire(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        paths.EnsureDirectories();
        var handle = new FileStream(
            Path.Combine(paths.RootDirectory, "profile.lock"),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None);
        return new ProfileLease(handle);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref handle, null)?.Dispose();
    }
}
