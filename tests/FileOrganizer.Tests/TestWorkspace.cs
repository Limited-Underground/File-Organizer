namespace FileOrganizer.Tests;

/// <summary>Leaves task-owned test fixtures for review; never touches real user profiles.</summary>
internal sealed class TestWorkspace
{
    public TestWorkspace()
    {
        var configuredRoot = Environment.GetEnvironmentVariable("FILE_ORGANIZER_TEST_ROOT");
        var repositoryRoot = configuredRoot is null ? FindRepositoryRoot() : Path.GetFullPath(configuredRoot);
        if (!File.Exists(Path.Combine(repositoryRoot, "docs", "ARCHITECTURE.md")) ||
            !File.Exists(Path.Combine(repositoryRoot, "tests", "FileOrganizer.Tests", "FileOrganizer.Tests.csproj")))
        {
            throw new InvalidOperationException("Test root must identify the File Organizer checkout.");
        }

        Root = Path.Combine(repositoryRoot, "artifacts", "FO-002-bootstrap-20261029", "test-data", Guid.NewGuid().ToString("N"));
        RejectReparseAncestors(Root);
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                throw new InvalidOperationException("Test fixtures cannot traverse reparse points.");
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "tests", "FileOrganizer.Tests", "FileOrganizer.Tests.csproj")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Set FILE_ORGANIZER_TEST_ROOT to the File Organizer checkout.");
    }
}
