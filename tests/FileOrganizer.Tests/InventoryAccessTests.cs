using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Xunit;

namespace FileOrganizer.Tests;

[SupportedOSPlatform("windows")]
public sealed class InventoryAccessTests
{
    private const string EvidenceTask = "FO-005-recovery-20261029";

    [Fact]
    public async Task ActualDeniedDirectoryRecordsCoverageGapPreservingSiblingsAndPublishedGeneration()
    {
        using var fixture = FixtureWorkspace.Create(evidenceTask: EvidenceTask);
        await new FixtureGenerator().GenerateAsync(fixture, new(7, [new("stable.bin", 51, "stable")]));
        var stableBefore = ObservePayload(fixture, "stable.bin");
        // Never use EnsureDirectory here: a retained list-authorized handle would survive ACL changes.
        var deniedPath = Path.Combine(fixture.Root, "denied");
        Directory.CreateDirectory(deniedPath);
        var hiddenPath = Path.Combine(deniedPath, "hidden.bin");
        byte[] hiddenBytes = [81, 82, 83, 84];
        File.WriteAllBytes(hiddenPath, hiddenBytes);
        var hiddenTime = File.GetLastWriteTimeUtc(hiddenPath);
        var directory = new DirectoryInfo(deniedPath);
        var original = directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        var descriptor = original.GetSecurityDescriptorBinaryForm();

        using var stateWorkspace = FixtureWorkspace.Create(evidenceTask: EvidenceTask);
        File.WriteAllBytes(Path.Combine(stateWorkspace.Root, "denied-original-security-descriptor.bin"), descriptor);
        var paths = new AppDataPaths(Path.Combine(stateWorkspace.Root, "application-state"));
        using var lease = ProfileLease.Acquire(paths);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(paths);
        var source = new FixtureInventorySource(fixture);
        var published = await new InventoryIndexer(source, store).RunAsync(batchSize: 2);
        Assert.True(published.Published);

        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new InvalidOperationException("The fixture ACL test needs the current user SID.");
        var restricted = directory.GetAccessControl(AccessControlSections.Access);
        restricted.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.ListDirectory,
            InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        try
        {
            directory.SetAccessControl(restricted);
            var captured = await source.CaptureAsync(CancellationToken.None);
            var denied = Assert.Single(captured.Entries, entry => entry.RelativePath == "denied");
            Assert.Equal(InventoryEntryKind.Directory, denied.Kind);
            Assert.Equal(0, denied.Length);
            Assert.NotNull(denied.ErrorCode);
            Assert.StartsWith("inventory.open.", denied.ErrorCode);
            Assert.Contains("C0000022", denied.ErrorCode);
            Assert.DoesNotContain(captured.Entries, entry => entry.RelativePath.StartsWith("denied/", StringComparison.Ordinal));
            Assert.Contains(captured.Entries, entry => entry.RelativePath == "stable.bin" && entry.ErrorCode is null);
            var incomplete = await new InventoryIndexer(source, store).RunAsync(batchSize: 1);
            Assert.Equal(InventoryRunStatus.Incomplete, incomplete.Status);
            Assert.False(incomplete.Published);
            var persisted = await new SqliteInventoryStore(paths).ReadStateAsync(CancellationToken.None);
            Assert.Equal(published.GenerationId, Assert.Single(persisted.Roots).GenerationId);
            Assert.Contains(persisted.Runs, run => run.GenerationId == incomplete.GenerationId && run.Status == InventoryRunStatus.Incomplete);
            Assert.Equal(stableBefore, ObservePayload(fixture, "stable.bin"));
        }
        finally
        {
            var restore = new DirectorySecurity();
            restore.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access);
            directory.SetAccessControl(restore);
            Assert.Equal(descriptor, directory.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group).GetSecurityDescriptorBinaryForm());
        }

        Assert.Equal(hiddenBytes, File.ReadAllBytes(hiddenPath));
        Assert.Equal(hiddenTime, File.GetLastWriteTimeUtc(hiddenPath));
        Assert.Equal(stableBefore, ObservePayload(fixture, "stable.bin"));
        Assert.Contains((await source.CaptureAsync(CancellationToken.None)).Entries, entry => entry.RelativePath == "denied/hidden.bin" && entry.ErrorCode is null);
    }

    [Fact]
    public async Task SameIdentityMutationBetweenEnumerationAndOpenIsTypedAndCannotReplacePublishedRoot()
    {
        using var fixture = FixtureWorkspace.Create(evidenceTask: EvidenceTask);
        await new FixtureGenerator().GenerateAsync(fixture, new(8, [new("stable.bin", 73, "stable")]));
        var stableBefore = ObservePayload(fixture, "stable.bin");
        var mutablePath = Path.Combine(fixture.Root, "mutable.bin");
        File.WriteAllBytes(mutablePath, [1, 2, 3]);
        var source = new FixtureInventorySource(fixture);
        var initial = await source.CaptureAsync(CancellationToken.None);
        var initialMutable = Assert.Single(initial.Entries, entry => entry.RelativePath == "mutable.bin");

        using var stateWorkspace = FixtureWorkspace.Create(evidenceTask: EvidenceTask);
        var paths = new AppDataPaths(Path.Combine(stateWorkspace.Root, "application-state"));
        using var lease = ProfileLease.Acquire(paths);
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store = new SqliteInventoryStore(paths);
        var published = await new InventoryIndexer(source, store).RunAsync(batchSize: 1);
        var changed = false;
        byte[] changedBytes = [4, 5, 6, 7, 8, 9];
        var mutationSource = new FixtureInventorySource(fixture, relative =>
        {
            if (relative != "mutable.bin" || changed) return;
            changed = true;
            // Dedicated unplanned leaf: generated payload handles and bytes remain untouched.
            File.WriteAllBytes(mutablePath, changedBytes);
            File.SetLastWriteTimeUtc(mutablePath, initialMutable.ModifiedUtc.AddSeconds(10));
        });
        var observed = await mutationSource.CaptureAsync(CancellationToken.None);
        Assert.True(changed);
        var stale = Assert.Single(observed.Entries, entry => entry.RelativePath == "mutable.bin");
        Assert.Equal("inventory.entry_changed", stale.ErrorCode);
        Assert.Equal(initialMutable.ObjectIdentity, stale.ObjectIdentity);
        var incomplete = await store.IndexAsync(observed, 1, CancellationToken.None, recapture: source.CaptureAsync);
        Assert.Equal(InventoryRunStatus.Incomplete, incomplete.Status);
        Assert.False(incomplete.Published);
        Assert.Equal(published.GenerationId, Assert.Single((await store.ReadStateAsync(CancellationToken.None)).Roots).GenerationId);
        Assert.Equal(stableBefore, ObservePayload(fixture, "stable.bin"));
        Assert.Equal(changedBytes, File.ReadAllBytes(mutablePath));
        var fresh = Assert.Single((await source.CaptureAsync(CancellationToken.None)).Entries, entry => entry.RelativePath == "mutable.bin");
        Assert.Null(fresh.ErrorCode);
        Assert.Equal(initialMutable.ObjectIdentity, fresh.ObjectIdentity);
        Assert.Equal(changedBytes.LongLength, fresh.Length);
    }

    private static (string Digest, DateTime ModifiedUtc) ObservePayload(FixtureWorkspace workspace, string relative)
        => (Convert.ToHexString(SHA256.HashData(workspace.ReadOwnedBytes(relative))), File.GetLastWriteTimeUtc(workspace.ResolveOwnedPath(relative)));
}
