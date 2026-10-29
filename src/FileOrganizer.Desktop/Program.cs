using Avalonia;
using FileOrganizer.Application;

namespace FileOrganizer.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            App.Options = LaunchOptions.Parse(args);
            return AppBuilder.Configure<App>().UsePlatformDetect().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception exception)
        {
            // Never print raw exception text: native/database messages may contain private paths.
            Console.Error.WriteLine($"FO_FATAL_STARTUP ({exception.GetType().Name})");
            return 1;
        }
    }
}

internal sealed record LaunchOptions(bool SmokeTest, string? DataRoot, bool AutoClose = true)
{
    public static LaunchOptions Parse(string[] args)
    {
        if (args.Length == 0) return new(false, null);
        if ((args.Length == 3 || (args.Length == 4 && args[3] == "--stay-open")) && args[0] == "--smoke-test" && args[1] == "--data-root" && Path.IsPathFullyQualified(args[2]))
        {
            var root = Path.GetFullPath(args[2]);
            ValidateFixturePath(root);
            return new(true, root, args.Length == 3);
        }
        throw new ArgumentException("Use no arguments, or --smoke-test --data-root <absolute fixture directory>.");
    }

    private static void ValidateFixturePath(string root)
    {
        if (root.StartsWith("\\\\", StringComparison.Ordinal) || root == Path.GetPathRoot(root))
            throw new ArgumentException("Smoke tests require a local marked disposable directory.");
        var realProfile = AppDataPaths.CreateDefault().RootDirectory;
        if (root.Equals(realProfile, StringComparison.OrdinalIgnoreCase) || root.StartsWith(realProfile + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Smoke tests cannot use the normal application profile.");
        var marked = false;
        for (var current = new DirectoryInfo(root); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new ArgumentException("Smoke tests cannot traverse reparse points.");
            var marker = Path.Combine(current.FullName, ".file-organizer-smoke-root");
            if (current.FullName != root && File.Exists(marker) && File.ReadAllText(marker).Trim() == "File Organizer disposable validation")
                marked = true;
        }
        if (!marked) throw new ArgumentException("A disposable fixture ancestor marker is required.");
        if (Directory.Exists(root))
        {
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.TryPop(out var directory))
            {
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new ArgumentException("Smoke fixture contains a reparse point.");
                    if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
                }
            }
        }
    }
}
