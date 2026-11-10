// T-0067: disposable SQLite only, no real source adapter.
using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class T0067InventoryComparisonTests
{
    private static InventoryEntry Entry(string path, string id, long length = 1) =>
        new(path, InventoryEntryKind.File, id, length, new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc),128);
    private static InventorySnapshot Truth(params InventoryEntry[] entries) => new("comparison-fixture", entries);
    private sealed record Pair(AppDataPaths Paths, InventoryReconciliationState Before, InventoryReconciliationState After);
    private static async Task<InventoryReconciliationState> Commit(AppDataPaths paths, InventorySnapshot truth,
        InventoryReconciliationState? prior = null, InventoryIdentityEvidence proof = InventoryIdentityEvidence.SuppliedStableWithinJournal,
        string journal = "journal-one")
    {
        var store = new SqliteInventoryStore(paths); InventoryCoverageState boundary;
        if (prior is null)
        {
            await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
            await store.IndexAsync(truth,1,CancellationToken.None);
            var state = await store.ReadStateAsync(CancellationToken.None);
            boundary = InventoryCoverageState.Create(Assert.Single(state.Roots),Assert.Single(state.Runs));
        }
        else boundary = InventoryCoverageState.Restore(prior.Root,prior.Scan,prior.Receipt.Checkpoint,prior.Receipt,prior.Floor);
        boundary = boundary.Apply(new("comparison-fixture",boundary.LastNoticeSequence+1,
            prior is not null && prior.Receipt.Checkpoint!.JournalIdentity != journal ? InventoryNoticeKind.JournalDiscontinuity : InventoryNoticeKind.Changed));
        var plan = new InventoryReconciliationPlan(Guid.NewGuid().ToString("N"),boundary.Export(),journal,
            (prior?.Receipt.Checkpoint?.NextUsn ?? 0)+10,SuppliedInventoryCoverage.Complete,proof);
        return await store.ReconcileAsync(truth,plan,1,CancellationToken.None,_=>Task.FromResult(truth));
    }
    private static async Task<Pair> Seed(InventorySnapshot before, InventorySnapshot after,
        InventoryIdentityEvidence proof = InventoryIdentityEvidence.SuppliedStableWithinJournal, string afterJournal = "journal-one")
    {
        var paths = new AppDataPaths(new TestWorkspace().Root);
        var a = await Commit(paths,before,proof:proof);
        var b = await Commit(paths,after,a,proof,afterJournal);
        return new(paths,a,b);
    }
    private static InventoryComparisonRequest Request(Pair pair) => new("comparison-fixture",pair.Before.Root.GenerationId,pair.After.Root.GenerationId);
    private static Task<InventoryComparisonResult> Compare(Pair pair, InventoryComparisonRequest? request = null) =>
        new SqliteInventoryStore(pair.Paths).CompareAsync(request??Request(pair));
    private static SqliteConnection Open(AppDataPaths paths)
    {
        var c = new SqliteConnection(new SqliteConnectionStringBuilder {DataSource=paths.DatabasePath,Pooling=false}.ToString()); c.Open(); return c;
    }
    private static object? Sql(AppDataPaths paths,string text)
    {using var c=Open(paths);using var cmd=c.CreateCommand();cmd.CommandText=text;return cmd.ExecuteScalar();}

    [Theory]
    [InlineData("added",InventoryDifferenceKind.Added)]
    [InlineData("removed",InventoryDifferenceKind.Removed)]
    [InlineData("renamed",InventoryDifferenceKind.Renamed)]
    [InlineData("changed",InventoryDifferenceKind.Changed)]
    public async Task ProvedCompleteIdentityClassifiesEachChange(string scenario, InventoryDifferenceKind kind)
    {
        var a=Truth(Entry("keep","keep"),Entry("old","a"));
        var b=scenario switch {
            "added"=>Truth(Entry("keep","keep"),Entry("old","a"),Entry("new","b")),
            "removed"=>Truth(Entry("keep","keep")),
            "renamed"=>Truth(Entry("keep","keep"),Entry("new","a")),
            _=>Truth(Entry("keep","keep"),Entry("old","a",9))};
        var pair=await Seed(a,b); var change=Assert.Single((await Compare(pair)).Differences);
        Assert.Equal(kind,change.Kind);Assert.False((await Compare(pair)).IsAtomicSnapshot);
    }
    [Fact]
    public async Task DifferentIdentityIsRemoveAndAddNeverRename()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","b")));
        Assert.Equal(new[]{InventoryDifferenceKind.Added,InventoryDifferenceKind.Removed},
            (await Compare(pair)).Differences.Select(d=>d.Kind).Order().ToArray());
    }
    [Fact]
    public async Task RenameAndMetadataChangeAreBothRetainedAndSwapsFollowIdentity()
    {
        var pair=await Seed(Truth(Entry("left","a"),Entry("right","b")),Truth(Entry("right","a",5),Entry("left","b")));
        var changes=(await Compare(pair)).Differences;Assert.Equal(2,changes.Count);
        Assert.All(changes,d=>Assert.Equal(InventoryDifferenceKind.Renamed,d.Kind));
        Assert.True(Assert.Single(changes,d=>d.BeforePath=="left").MetadataChanged);
    }
    [Theory]
    [InlineData(SuppliedInventoryCoverage.Unknown)] [InlineData(SuppliedInventoryCoverage.Offline)]
    public async Task CoverageCanOnlyDowngradeAndNeverInfersMissingRemoval(SuppliedInventoryCoverage coverage)
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")));
        foreach(var request in new[]{Request(pair) with {BeforeCoverage=coverage},Request(pair) with {AfterCoverage=coverage}})
            Assert.All((await Compare(pair,request)).Differences,d=>Assert.Equal(InventoryDifferenceKind.Unknown,d.Kind));
    }
    [Fact]
    public async Task UnprovedIdentityRemainsUnknownEvenWithCompleteCoverage()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")),InventoryIdentityEvidence.Unverified);
        Assert.All((await Compare(pair)).Differences,d=>Assert.Equal(InventoryDifferenceKind.Unknown,d.Kind));
        Assert.Equal(2,(await Compare(pair)).Differences.Count);
    }
    [Fact]
    public async Task AmbiguousSharedIdentityNeverBecomesRenameOrRemoval()
    {
        var pair=await Seed(Truth(Entry("one","a"),Entry("two","a")),Truth(Entry("three","a")));
        Assert.Equal(3,(await Compare(pair)).Differences.Count);
        Assert.All((await Compare(pair)).Differences,d=>Assert.Equal(InventoryDifferenceKind.Unknown,d.Kind));
    }
    [Fact]
    public async Task SelfComparisonValidatesAndIsEmpty()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")),InventoryIdentityEvidence.Unverified);
        Assert.Empty((await Compare(pair,Request(pair) with {AfterGeneration=pair.Before.Root.GenerationId,BeforeCoverage=SuppliedInventoryCoverage.Offline})).Differences);
    }
    [Fact]
    public async Task JournalAndManagedRootMismatchAreRefused()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")),afterJournal:"journal-two");
        Assert.Equal("inventory.comparison_incompatible",(await Assert.ThrowsAsync<InventoryIndexException>(()=>Compare(pair))).Code);
        await Assert.ThrowsAsync<InventoryIndexException>(()=>Compare(pair,Request(pair) with {RootIdentity="different-root"}));
    }
    [Theory]
    [InlineData("UPDATE InventoryScan SET Status='Incomplete' WHERE GenerationId=(SELECT GenerationId FROM InventoryPublishedRoot);")]
    [InlineData("UPDATE InventoryScan SET CommittedEntries=0 WHERE GenerationId=(SELECT GenerationId FROM InventoryPublishedRoot);")]
    [InlineData("DELETE FROM InventoryDirectoryEntry WHERE GenerationId=(SELECT GenerationId FROM InventoryPublishedRoot);")]
    public async Task IncompleteOrMissingRowsAreRefused(string corrupt)
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")));Sql(pair.Paths,corrupt);
        Assert.Equal("inventory.comparison_incomplete",(await Assert.ThrowsAsync<InventoryIndexException>(()=>Compare(pair))).Code);
    }
    [Fact]
    public async Task RawUnmanagedCompletedGenerationCannotQualifyAndReadDoesNotMigrate()
    {
        var paths=new AppDataPaths(new TestWorkspace().Root);await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var raw=await new SqliteInventoryStore(paths).IndexAsync(Truth(Entry("old","a")),1,CancellationToken.None);
        var request=new InventoryComparisonRequest("comparison-fixture",raw.GenerationId,raw.GenerationId);
        Assert.Equal("inventory.comparison_schema_unsupported",(await Assert.ThrowsAsync<InventoryIndexException>(()=>new SqliteInventoryStore(paths).CompareAsync(request))).Code);
        Assert.Equal(2L,Sql(paths,"PRAGMA user_version;"));
        using(var c=Open(paths)) InventoryComparisonSchema.Ensure(c,paths.DataDirectory,CancellationToken.None);
        Assert.Equal("inventory.comparison_uncommitted",(await Assert.ThrowsAsync<InventoryIndexException>(()=>new SqliteInventoryStore(paths).CompareAsync(request))).Code);
    }
    [Theory]
    [InlineData(4)] [InlineData(6)]
    public async Task UnsupportedSchemaIsRefusedWithoutMutation(int version)
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")));Sql(pair.Paths,"PRAGMA user_version="+version+";");
        Assert.Equal("inventory.comparison_schema_unsupported",(await Assert.ThrowsAsync<InventoryIndexException>(()=>Compare(pair))).Code);
        Assert.Equal((long)version,Sql(pair.Paths,"PRAGMA user_version;"));
    }
    [Fact]
    public async Task HistoricalComparisonIsDeterministicAndByteReadOnlyAfterFurtherPublication()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")));
        var third=await Commit(pair.Paths,Truth(Entry("third","a")),pair.After);
        var files=Directory.GetFiles(pair.Paths.DataDirectory,"*",SearchOption.AllDirectories).Order().ToArray();
        var hashes=files.Select(p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        var checkpoint=await new SqliteInventoryStore(pair.Paths).ReadReconciliationAsync("comparison-fixture",CancellationToken.None);
        var first=await Compare(pair);var second=await Compare(pair);
        Assert.Equal(JsonSerializer.Serialize(first),JsonSerializer.Serialize(second));
        Assert.Equal(InventoryDifferenceKind.Renamed,Assert.Single(first.Differences).Kind);
        Assert.Equal(checkpoint,await new SqliteInventoryStore(pair.Paths).ReadReconciliationAsync("comparison-fixture",CancellationToken.None));
        Assert.Equal(third.Floor,checkpoint!.Floor);
        Assert.Equal(files,Directory.GetFiles(pair.Paths.DataDirectory,"*",SearchOption.AllDirectories).Order().ToArray());
        Assert.Equal(hashes,files.Select(p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray());
        Assert.Equal(3L,Sql(pair.Paths,"SELECT count(*) FROM InventoryGenerationCommit;"));
        Assert.Equal("ok",Sql(pair.Paths,"PRAGMA integrity_check;"));
    }
    [Fact]
    public async Task V4MigrationPreservesChecksumsBackupAndExactRetryWithoutInventingProof()
    {
        var paths=new AppDataPaths(new TestWorkspace().Root);var truth=Truth(Entry("old","a"));
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);var store=new SqliteInventoryStore(paths);
        await store.IndexAsync(truth,1,CancellationToken.None);var state=await store.ReadStateAsync(CancellationToken.None);
        var root=Assert.Single(state.Roots);var scan=Assert.Single(state.Runs);
        var plan=new InventoryReconciliationPlan(Guid.NewGuid().ToString("N"),InventoryCoverageState.Create(root,scan).Export(),"journal-one",10,SuppliedInventoryCoverage.Complete);
        using(var c=Open(paths)) InventoryReconciliationSchema.Ensure(c,paths.DataDirectory,CancellationToken.None);
        // Actual legacy hash format and v4 row, with no invented older witnesses.
        var fingerprint=PreparedInventorySnapshot.Create(truth).Fingerprint;
        var legacyHash=Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new {plan=new {plan.OperationId,plan.Before,plan.JournalIdentity,plan.NextUsn,plan.Coverage},Fingerprint=fingerprint})));
        using(var c=Open(paths)) {using var cmd=c.CreateCommand();cmd.CommandText="INSERT INTO InventoryReconciliation VALUES($root,$gen,$fp,$op,$hash,'journal-one',10,0,0);";
            cmd.Parameters.AddWithValue("$root",root.RootIdentity);cmd.Parameters.AddWithValue("$gen",root.GenerationId);cmd.Parameters.AddWithValue("$fp",fingerprint);cmd.Parameters.AddWithValue("$op",plan.OperationId);cmd.Parameters.AddWithValue("$hash",legacyHash);cmd.ExecuteNonQuery();}
        var checksums=Sql(paths,"SELECT group_concat(Checksum,',') FROM (SELECT Checksum FROM SchemaMigration ORDER BY Version);");
        var adopted=await store.ReconcileAsync(truth,plan,1,CancellationToken.None,_=>Task.FromResult(truth));
        Assert.Equal(legacyHash,adopted.RequestHash);Assert.Equal(5L,Sql(paths,"PRAGMA user_version;"));
        Assert.Equal(checksums,Sql(paths,"SELECT group_concat(Checksum,',') FROM (SELECT Checksum FROM SchemaMigration WHERE Version<=4 ORDER BY Version);"));
        var backup=Assert.Single(Directory.GetFiles(Path.Combine(paths.DataDirectory,"backups"),"before-inventory-v5-*.db"));
        using(var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=backup,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString()))
        {c.Open();SqliteApplicationStore.ValidateMigration(c,null,4);using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA quick_check;";Assert.Equal("ok",cmd.ExecuteScalar());}
        Assert.Equal(1L,Sql(paths,"SELECT count(*) FROM InventoryGenerationCommit;"));
        Assert.Equal(1L,Sql(paths,"SELECT Coverage FROM InventoryGenerationCommit;"));
        Assert.Equal(0L,Sql(paths,"SELECT IdentityEvidence FROM InventoryGenerationCommit;"));
        var after=await Commit(paths,Truth(Entry("new","a")),adopted);
        Assert.All((await Compare(new(paths,adopted,after))).Differences,d=>Assert.Equal(InventoryDifferenceKind.Unknown,d.Kind));
        Assert.Throws<SqliteException>(()=>Sql(paths,"UPDATE InventoryGenerationCommit SET Coverage=0;"));
        Assert.Throws<SqliteException>(()=>Sql(paths,"DELETE FROM InventoryGenerationCommit;"));
    }
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task CommitWitnessRollsBackWithEveryPublicationFault(int stage)
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")));
        var boundary=InventoryCoverageState.Restore(pair.After.Root,pair.After.Scan,pair.After.Receipt.Checkpoint,pair.After.Receipt,pair.After.Floor);
        var truth=Truth(Entry("third","a"));
        var store=new SqliteInventoryStore(pair.Paths,p=>{if((int)p==stage)throw new InvalidOperationException("injected");});
        var plan=new InventoryReconciliationPlan(Guid.NewGuid().ToString("N"),boundary.Export(),"journal-one",30,SuppliedInventoryCoverage.Complete,InventoryIdentityEvidence.SuppliedStableWithinJournal);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>store.ReconcileAsync(truth,plan,1,CancellationToken.None,_=>Task.FromResult(truth)));
        Assert.Equal(2L,Sql(pair.Paths,"SELECT count(*) FROM InventoryGenerationCommit;"));
        Assert.Equal(pair.After,await new SqliteInventoryStore(pair.Paths).ReadReconciliationAsync("comparison-fixture",CancellationToken.None));
    }
    [Fact]
    public async Task CancelledComparisonHasNoSideEffects()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")));using var c=new CancellationTokenSource();c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new SqliteInventoryStore(pair.Paths).CompareAsync(Request(pair),c.Token));
        Assert.Equal(2L,Sql(pair.Paths,"SELECT count(*) FROM InventoryGenerationCommit;"));
    }

    [Fact]
    public async Task SamePathReplacementIsChangedAndExcludedIdentityIsUnknown()
    {
        var pair=await Seed(Truth(Entry("same","a")),Truth(Entry("same","b")));
        Assert.Equal(InventoryDifferenceKind.Changed,Assert.Single((await Compare(pair)).Differences).Kind);
        var excluded=Entry("link","") with {Kind=InventoryEntryKind.ExcludedReparse};
        var uncertain=await Seed(Truth(excluded),Truth(excluded,Entry("new","b")));
        Assert.Equal(InventoryDifferenceKind.Unknown,Assert.Single((await Compare(uncertain)).Differences,d=>d.BeforePath=="link").Kind);
    }
    [Fact]
    public async Task ConflictingKindUnderSameIdentityIsNeverRenameOrRemoval()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a",0) with {Kind=InventoryEntryKind.Directory}));
        Assert.All((await Compare(pair)).Differences,d=>Assert.Equal(InventoryDifferenceKind.Unknown,d.Kind));
    }
    [Fact]
    public async Task ReusedGenerationAcrossJournalsIsAmbiguousRatherThanSilentlySelectingLatestWitness()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")));
        _=await Commit(pair.Paths,Truth(Entry("new","a")),pair.After);
        var current=await new SqliteInventoryStore(pair.Paths).ReadReconciliationAsync("comparison-fixture",CancellationToken.None);
        _=await Commit(pair.Paths,Truth(Entry("new","a")),current!,journal:"journal-two");
        Assert.Equal("inventory.comparison_incompatible",(await Assert.ThrowsAsync<InventoryIndexException>(()=>Compare(pair))).Code);
    }
    [Fact]
    public async Task V5MigrationFailureRollsBackAndRetainsItsVerifiedV4Backup()
    {
        var paths=new AppDataPaths(new TestWorkspace().Root);var truth=Truth(Entry("old","a"));
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        await new SqliteInventoryStore(paths).IndexAsync(truth,1,CancellationToken.None);
        using(var c=Open(paths)) InventoryReconciliationSchema.Ensure(c,paths.DataDirectory,CancellationToken.None);
        using(var c=Open(paths)) Assert.Throws<InvalidOperationException>(()=>InventoryComparisonSchema.Ensure(c,paths.DataDirectory,CancellationToken.None,()=>throw new InvalidOperationException("injected")));
        Assert.Equal(4L,Sql(paths,"PRAGMA user_version;"));
        Assert.Equal(0L,Sql(paths,"SELECT count(*) FROM sqlite_master WHERE name='InventoryGenerationCommit';"));
        Assert.Equal(4L,Sql(paths,"SELECT count(*) FROM SchemaMigration;"));
        Assert.Single(Directory.GetFiles(Path.Combine(paths.DataDirectory,"backups"),"before-inventory-v5-*.db"));
    }
    [Fact]
    public async Task DefaultIdentityProofCannotBeUpgradedByComparisonOrLaterWitness()
    {
        var pair=await Seed(Truth(Entry("old","a")),Truth(Entry("new","a")),InventoryIdentityEvidence.Unverified);
        _=await Commit(pair.Paths,Truth(Entry("new","a")),pair.After);
        Assert.All((await Compare(pair)).Differences,d=>Assert.Equal(InventoryDifferenceKind.Unknown,d.Kind));
    }
}
