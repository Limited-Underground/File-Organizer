using FileOrganizer.Application;
using FileOrganizer.Desktop;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class LaunchOptionsTests
{
    [Fact]
    public void SmokeModeAcceptsGeneratedChildOfMarkedDisposableRoot()
    {
        var workspace = new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Root, ".file-organizer-smoke-root"), "File Organizer disposable validation");
        var child = Path.Combine(workspace.Root, "smoke-profile");
        var options = LaunchOptions.Parse(["--smoke-test", "--data-root", child]);
        Assert.True(options.SmokeTest);
        Assert.Equal(child, options.DataRoot);
        Assert.False(Directory.Exists(child));
    }

    [Fact]
    public void SmokeModeRejectsUnmarkedWorkspace()
    {
        var workspace = new TestWorkspace();
        Assert.Throws<ArgumentException>(() =>
            LaunchOptions.Parse(["--smoke-test", "--data-root", Path.Combine(workspace.Root, "unmarked")]));
    }

    [Fact]
    public void SmokeModeRejectsNormalProfileWithoutTouchingIt()
    {
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(
            ["--smoke-test", "--data-root", AppDataPaths.CreateDefault().RootDirectory]));
    }

    [Fact]
    public void SmokeModeRejectsDriveRoot()
    {
        var driveRoot = Path.GetPathRoot(new TestWorkspace().Root)!;
        Assert.Throws<ArgumentException>(() => LaunchOptions.Parse(["--smoke-test", "--data-root", driveRoot]));
    }
}
