using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Tests.Fixtures;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class FixtureGenerationTests
{
    private static FixturePlan Plan(int seed = 104729) => new(seed,
        [new("empty.bin", 0, "empty"), new("nested/日本語.bin", 4097, "same"), new("duplicate.bin", 4097, "same")],
        ["empty-directory"]);

    [Fact]
    public async Task SeededRunsHaveIdenticalPayloadsManifestsAndFixedTimes()
    {
        using var first = FixtureWorkspace.Create();
        using var second = FixtureWorkspace.Create();
        var firstRun = await new FixtureGenerator().GenerateAsync(first, Plan());
        var secondRun = await new FixtureGenerator().GenerateAsync(second, Plan());
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(firstRun.CanonicalManifestBytes, secondRun.CanonicalManifestBytes);
        foreach (var file in Plan().Files)
        {
            Assert.Equal(first.ReadOwnedBytes(file.RelativePath), second.ReadOwnedBytes(file.RelativePath));
            Assert.Equal(file.Length, first.ReadOwnedBytes(file.RelativePath).LongLength);
            Assert.Equal(File.GetLastWriteTimeUtc(first.ResolveOwnedPath(file.RelativePath)),
                File.GetLastWriteTimeUtc(second.ResolveOwnedPath(file.RelativePath)));
        }
        Assert.Equal(first.ReadOwnedBytes("duplicate.bin"), first.ReadOwnedBytes("nested/日本語.bin"));
        Assert.Empty(first.ReadOwnedBytes("empty.bin"));
        Assert.True(Directory.Exists(first.ResolveOwnedPath("empty-directory")));
        Assert.Equal("Completed", firstRun.Receipt.Outcome);
        Assert.Equal(3, firstRun.Receipt.FilesCompleted);
        Assert.Equal(8194, firstRun.Receipt.BytesWritten);
        Assert.Equal(Convert.ToHexString(SHA256.HashData(firstRun.CanonicalManifestBytes)), firstRun.Receipt.ManifestSha256);
        Assert.Equal(firstRun.CanonicalManifestBytes, first.ReadOwnedBytes("manifest.json"));
        Assert.DoesNotContain(first.Root, System.Text.Encoding.UTF8.GetString(firstRun.CanonicalManifestBytes));
        var manifest = JsonSerializer.Deserialize<FixtureManifest>(firstRun.CanonicalManifestBytes)!;
        foreach (var entry in manifest.Files)
            Assert.Equal(entry.Sha256, Convert.ToHexString(SHA256.HashData(first.ReadOwnedBytes(entry.RelativePath))));
    }

    [Fact]
    public async Task ChangingSeedChangesPayloadAndCanonicalManifest()
    {
        using var first = FixtureWorkspace.Create();
        using var second = FixtureWorkspace.Create();
        var a = await new FixtureGenerator().GenerateAsync(first, Plan(1));
        var b = await new FixtureGenerator().GenerateAsync(second, Plan(2));
        Assert.False(a.CanonicalManifestBytes.SequenceEqual(b.CanonicalManifestBytes));
        Assert.False(first.ReadOwnedBytes("duplicate.bin").SequenceEqual(second.ReadOwnedBytes("duplicate.bin")));
    }

    [Fact]
    public async Task ManifestIsIndependentOfInputEnumerationOrder()
    {
        using var first = FixtureWorkspace.Create();
        using var second = FixtureWorkspace.Create();
        var plan = new FixturePlan(7, [new("b/data", 9, "b"), new("a/data", 11, "a")], ["z", "y"]);
        var reversed = new FixturePlan(plan.Seed, plan.Files.Reverse().ToArray(), plan.Directories!.Reverse().ToArray());
        var a = await new FixtureGenerator().GenerateAsync(first, plan);
        var b = await new FixtureGenerator().GenerateAsync(second, reversed);
        Assert.Equal(a.CanonicalManifestBytes, b.CanonicalManifestBytes);
    }

    [Fact]
    public async Task SecondOccurrenceFaultTriggersAfterExactlyOneCompletedFile()
    {
        using var workspace = FixtureWorkspace.Create();
        var error = await Assert.ThrowsAsync<FixtureInjectedException>(() => new FixtureGenerator().GenerateAsync(workspace, Plan(),
            new(FixtureCheckpoint.AfterFileCreate, 2, FixtureFaultAction.Throw)));
        Assert.Equal(2, error.Occurrence);
        var receipt = JsonSerializer.Deserialize<FixtureReceipt>(workspace.ReadOwnedBytes("receipt.json"))!;
        Assert.Equal(2, receipt.FilesCreated);
        Assert.Equal(1, receipt.FilesCompleted);
        Assert.Equal(2, receipt.CheckpointOccurrence);
        Assert.Equal("Failed", receipt.Outcome);
    }

    [Fact]
    public async Task UnreachableRequestedFaultCannotBeReportedAsSuccessfulRun()
    {
        using var workspace = FixtureWorkspace.Create();
        var error = await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(workspace, Plan(),
            new(FixtureCheckpoint.BeforeReceipt, 999, FixtureFaultAction.Throw)));
        Assert.Equal("fault.not_reached", error.Code);
        var receipt = JsonSerializer.Deserialize<FixtureReceipt>(workspace.ReadOwnedBytes("receipt.json"))!;
        Assert.Equal("Failed", receipt.Outcome);
        Assert.Equal("fault.not_reached", receipt.ErrorCode);
    }

    [Fact]
    public async Task WorkspaceCannotBeReusedAndOriginalReceiptRemainsIntact()
    {
        using var workspace = FixtureWorkspace.Create();
        await new FixtureGenerator().GenerateAsync(workspace, Plan());
        var receipt = workspace.ReadOwnedBytes("receipt.json");
        var error = await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(workspace, Plan()));
        Assert.Equal("generation.already_started", error.Code);
        Assert.Equal(receipt, workspace.ReadOwnedBytes("receipt.json"));
    }

    [Fact]
    public async Task ExpiredDurationBudgetStopsAtCheckpointAndPreservesFailureReceipt()
    {
        using var workspace = FixtureWorkspace.Create(new() { MaxDuration = TimeSpan.FromSeconds(1) });
        var generator = new FixtureGenerator((point, _) =>
        {
            if (point == FixtureCheckpoint.BeforeManifest) Thread.Sleep(1100);
        });
        var error = await Assert.ThrowsAsync<FixtureSafetyException>(() => generator.GenerateAsync(workspace, Plan()));
        Assert.Equal("quota.duration", error.Code);
        var receipt = JsonSerializer.Deserialize<FixtureReceipt>(workspace.ReadOwnedBytes("receipt.json"))!;
        Assert.Equal("Failed", receipt.Outcome);
        Assert.Equal("quota.duration", receipt.ErrorCode);
        Assert.Equal(0, receipt.FilesCreated);
    }

    public static IEnumerable<object[]> FaultCases()
    {
        foreach (var checkpoint in Enum.GetValues<FixtureCheckpoint>())
        foreach (var action in Enum.GetValues<FixtureFaultAction>())
            yield return [checkpoint.ToString(), action.ToString()];
    }

    [Theory]
    [MemberData(nameof(FaultCases))]
    public async Task EveryFaultPointPreservesHonestReceiptAndAvailableManifest(string checkpointName, string actionName)
    {
        var checkpoint = Enum.Parse<FixtureCheckpoint>(checkpointName);
        var action = Enum.Parse<FixtureFaultAction>(actionName);
        using var workspace = FixtureWorkspace.Create();
        var failure = await Record.ExceptionAsync(() => new FixtureGenerator().GenerateAsync(workspace, Plan(), new(checkpoint, 1, action)));
        Assert.NotNull(failure);
        if (action == FixtureFaultAction.Cancel) Assert.IsAssignableFrom<OperationCanceledException>(failure);
        else Assert.IsType<FixtureInjectedException>(failure);
        var receipt = JsonSerializer.Deserialize<FixtureReceipt>(workspace.ReadOwnedBytes("receipt.json"));
        Assert.NotNull(receipt);
        Assert.Equal(action == FixtureFaultAction.Cancel ? "Cancelled" : "Failed", receipt.Outcome);
        Assert.Equal(workspace.RunId, receipt.RunId);
        Assert.Equal(checkpoint.ToString(), receipt.LastCheckpoint);
        Assert.Equal(1, receipt.CheckpointOccurrence);
        if (checkpoint == FixtureCheckpoint.BeforeManifest)
            Assert.False(File.Exists(Path.Combine(workspace.Root, "manifest.json")));
        else
            Assert.NotEmpty(workspace.ReadOwnedBytes("manifest.json"));
    }

    [Fact]
    public async Task ExternalCancellationBeforeStartLeavesCancelledReceiptWithoutPayload()
    {
        using var workspace = FixtureWorkspace.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FixtureGenerator().GenerateAsync(workspace, Plan(), cancellationToken: cancellation.Token));
        Assert.Equal("Cancelled", JsonSerializer.Deserialize<FixtureReceipt>(workspace.ReadOwnedBytes("receipt.json"))!.Outcome);
        Assert.False(File.Exists(Path.Combine(workspace.Root, "duplicate.bin")));
    }

    [Fact]
    public async Task ExternalCancellationAtChunkBoundaryPreservesPartialReceipt()
    {
        using var workspace = FixtureWorkspace.Create();
        using var cancellation = new CancellationTokenSource();
        var generator = new FixtureGenerator((point, _) =>
        {
            if (point == FixtureCheckpoint.AfterChunkWrite) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => generator.GenerateAsync(workspace, Plan(), cancellationToken: cancellation.Token));
        var receipt = JsonSerializer.Deserialize<FixtureReceipt>(workspace.ReadOwnedBytes("receipt.json"))!;
        Assert.Equal("Cancelled", receipt.Outcome);
        Assert.True(receipt.BytesWritten > 0);
        Assert.True(receipt.FilesCompleted < Plan().Files.Count);
        Assert.NotEmpty(workspace.ReadOwnedBytes("manifest.json"));
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(1048577L)]
    [InlineData(long.MaxValue)]
    public async Task InvalidOrOversizedFileIsRejectedBeforePayload(long length)
    {
        using var workspace = FixtureWorkspace.Create();
        await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(workspace,
            new(1, [new("oversize.bin", length, "x")])));
        Assert.False(File.Exists(Path.Combine(workspace.Root, "oversize.bin")));
    }

    [Fact]
    public async Task FileCountAndTotalByteLimitsArePreflighted()
    {
        using var files = FixtureWorkspace.Create(new() { MaxFiles = 1 });
        await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(files,
            new(1, [new("a", 1, "a"), new("b", 1, "b")])));
        Assert.False(File.Exists(Path.Combine(files.Root, "a")));
        using var bytes = FixtureWorkspace.Create(new() { MaxTotalBytes = 10 });
        await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(bytes,
            new(1, [new("a", 6, "a"), new("b", 6, "b")])));
        Assert.False(File.Exists(Path.Combine(bytes.Root, "a")));
    }

    [Fact]
    public async Task DirectoryCountDepthAndCollisionArePreflighted()
    {
        using var directories = FixtureWorkspace.Create(new() { MaxDirectories = 1 });
        await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(directories,
            new(1, [new("one/two/file", 1, "a")])));
        Assert.False(Directory.Exists(Path.Combine(directories.Root, "one")));
        using var depth = FixtureWorkspace.Create(new() { MaxDepth = 2 });
        await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(depth,
            new(1, [new("one/two/file", 1, "a")])));
        using var collision = FixtureWorkspace.Create();
        await Assert.ThrowsAsync<FixtureSafetyException>(() => new FixtureGenerator().GenerateAsync(collision,
            new(1, [new("Same", 1, "a"), new("same", 1, "b")])));
        Assert.False(File.Exists(Path.Combine(collision.Root, "Same")));
    }
}
