namespace FileOrganizer.Application;

public sealed class AppDataPaths
{
    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }
    public string DataDirectory => Path.Combine(RootDirectory, "data");
    public string LogsDirectory => Path.Combine(RootDirectory, "logs");
    public string DatabasePath => Path.Combine(DataDirectory, "inventory.db");

    public static AppDataPaths CreateDefault()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
        {
            throw new InvalidOperationException("The local application data location is unavailable.");
        }

        return new AppDataPaths(Path.Combine(localData, "Limited Underground", "File Organizer"));
    }

    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
