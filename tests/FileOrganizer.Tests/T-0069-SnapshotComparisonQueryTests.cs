// T-0069: host application/query composition over disposable completed SQLite generations.
using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class T0069SnapshotComparisonQueryTests
{
    private static InventoryEntry Entry(string path,string id,long bytes=1)=>new(path,InventoryEntryKind.File,id,bytes,
        new DateTime(2020,1,2,0,0,0,DateTimeKind.Utc),128);
    private static InventorySnapshot Truth(params InventoryEntry[] entries)=>new("snapshot-fixture",entries);
    private static InventoryEntry Link()=>Entry("mystery.link","") with {Kind=InventoryEntryKind.ExcludedReparse};
    private sealed record Fixture(AppDataPaths Paths,InventoryReconciliationState Before,InventoryReconciliationState After);
    private static async Task<InventoryReconciliationState> Commit(AppDataPaths paths,InventorySnapshot truth,
        InventoryReconciliationState? prior=null,InventoryIdentityEvidence proof=InventoryIdentityEvidence.SuppliedStableWithinJournal)
    {
        var store=new SqliteInventoryStore(paths);InventoryCoverageState boundary;
        if(prior is null)
        {
            await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
            await store.IndexAsync(truth,1,CancellationToken.None);var state=await store.ReadStateAsync(CancellationToken.None);
            boundary=InventoryCoverageState.Create(Assert.Single(state.Roots),Assert.Single(state.Runs));
        }
        else boundary=InventoryCoverageState.Restore(prior.Root,prior.Scan,prior.Receipt.Checkpoint,prior.Receipt,prior.Floor);
        boundary=boundary.Apply(new("snapshot-fixture",boundary.LastNoticeSequence+1,InventoryNoticeKind.Changed));
        var plan=new InventoryReconciliationPlan(Guid.NewGuid().ToString("N"),boundary.Export(),"journal-snapshot",
            (prior?.Receipt.Checkpoint!.NextUsn??0)+10,SuppliedInventoryCoverage.Complete,proof);
        return await store.ReconcileAsync(truth,plan,1,CancellationToken.None,_=>Task.FromResult(truth));
    }
    private static async Task<Fixture> Seed(InventoryIdentityEvidence proof=InventoryIdentityEvidence.SuppliedStableWithinJournal)
    {
        var paths=new AppDataPaths(new TestWorkspace().Root);
        var before=await Commit(paths,Truth(Entry("keep","keep"),Entry("gone.bin","gone",5),Entry("oldname.txt","rename",7),
            Entry("changed.TXT","changed",1),Entry("replace.txt","old-id",9),Link()),proof:proof);
        var after=await Commit(paths,Truth(Entry("keep","keep"),Entry("added.txt","added",4),Entry("newname.txt","rename",7),
            Entry("changed.TXT","changed",10),Entry("replace.txt","new-id",9),Link()),before,proof);
        return new(paths,before,after);
    }
    private static SnapshotComparisonSelection Select(Fixture f,InventoryDifferenceKind? kind=null,IndexedInventoryFilter? filter=null)=>
        new(new("snapshot-fixture",f.Before.Root.GenerationId,f.After.Root.GenerationId),kind,filter);
    private static InventorySnapshotComparisons Query(Fixture f,IIndexedInventoryReader? reader=null)=>
        new(new SqliteInventoryStore(f.Paths),reader??new SqliteIndexedInventoryReader(f.Paths));
    private static void Execute(AppDataPaths paths,string sql)
    {using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=paths.DatabasePath,Pooling=false}.ToString());c.Open();using var cmd=c.CreateCommand();cmd.CommandText=sql;cmd.ExecuteNonQuery();}

    [Fact]
    public async Task ExactSelectedSnapshotsExposeFiveSeparateCountsAndHonestLabels()
    {
        var f=await Seed();var view=await Query(f).OpenAsync(Select(f));
        Assert.Equal(Select(f).Snapshots,view.Selection.Snapshots);Assert.Equal("journal-snapshot",view.JournalIdentity);
        Assert.Equal(new SnapshotComparisonCounts(1,1,1,2,1),view.TotalCounts);Assert.Equal(view.TotalCounts,view.FilteredCounts);
        Assert.Equal(6,view.TotalCounts.Total);Assert.False(view.IsAtomicSnapshot);Assert.False(view.LiveAvailabilityKnown);
    }
    [Theory]
    [InlineData(InventoryDifferenceKind.Added,1)] [InlineData(InventoryDifferenceKind.Removed,1)]
    [InlineData(InventoryDifferenceKind.Renamed,1)] [InlineData(InventoryDifferenceKind.Changed,2)] [InlineData(InventoryDifferenceKind.Unknown,1)]
    public async Task EveryClassificationIsVisibleAndFilteringNeverRelabelsIt(InventoryDifferenceKind kind,int count)
    {
        var f=await Seed();var view=await Query(f).OpenAsync(Select(f,kind));var page=view.ReadPage();
        Assert.Equal(count,page.Rows.Count);Assert.All(page.Rows,r=>Assert.Equal(kind,r.Kind));
        Assert.Equal(6,page.TotalCounts.Total);Assert.Equal(count,page.FilteredCounts.Total);
        Assert.Equal(kind==InventoryDifferenceKind.Unknown?1:0,page.FilteredCounts.Unknown);
    }
    [Fact]
    public async Task PagesAreStableOrderedNonoverlappingAndBoundToTheCompleteView()
    {
        var f=await Seed();var query=Query(f);var view=await query.OpenAsync(Select(f));var again=await query.OpenAsync(Select(f));
        Assert.Equal(view.ViewIdentity,again.ViewIdentity);SnapshotComparisonCursor? cursor=null;var rows=new List<InventoryDifference>();
        do {var page=view.ReadPage(2,cursor);Assert.Equal(JsonSerializer.Serialize(page),JsonSerializer.Serialize(again.ReadPage(2,cursor)));
            rows.AddRange(page.Rows);cursor=page.NextCursor;} while(cursor is not null);
        Assert.Equal(6,rows.Count);Assert.Equal(6,rows.Distinct().Count());
        Assert.Equal(view.ReadPage(200).Rows.ToArray(),rows.ToArray());
        Assert.Equal(rows.OrderBy(r=>r.BeforePath??r.AfterPath,StringComparer.Ordinal).ThenBy(r=>r.AfterPath,StringComparer.Ordinal).ThenBy(r=>r.Kind).ToArray(),rows.ToArray());
        var first=view.ReadPage(2);Assert.Throws<InventoryIndexException>(()=>view.ReadPage(1,first.NextCursor));
        var filtered=await query.OpenAsync(Select(f,InventoryDifferenceKind.Changed));
        Assert.Throws<InventoryIndexException>(()=>filtered.ReadPage(2,first.NextCursor));
        var searched=await query.OpenAsync(Select(f,filter:new(Text:"txt")));
        Assert.Throws<InventoryIndexException>(()=>searched.ReadPage(2,first.NextCursor));
    }
    [Theory]
    [InlineData("old-name",1)] [InlineData("new-name",1)] [InlineData("extension",4)] [InlineData("case-sensitive",3)]
    [InlineData("minimum",2)] [InlineData("maximum",2)] [InlineData("date",5)] [InlineData("kind",1)]
    [InlineData("unavailable",1)] [InlineData("literal",0)] [InlineData("combined",1)]
    public async Task ExistingSavedQueryPredicatesComposeWithEitherWholeEndpoint(string scenario,int count)
    {
        var f=await Seed();var day=new DateOnly(2020,1,2);
        var filter=scenario switch {
            "old-name"=>new IndexedInventoryFilter(Text:"oldname"),
            "new-name"=>new IndexedInventoryFilter(Text:"newname",Scope:IndexedInventorySearchScope.Path),
            "extension"=>new IndexedInventoryFilter(Extension:".TXT"),
            "case-sensitive"=>new IndexedInventoryFilter(Extension:".txt",CaseMode:IndexedInventoryCaseMode.MatchCase),
            "minimum"=>new IndexedInventoryFilter(MinBytes:8),
            "maximum"=>new IndexedInventoryFilter(MaxBytes:4),
            "date"=>new IndexedInventoryFilter(ModifiedFrom:day,ModifiedThrough:day),
            "kind"=>new IndexedInventoryFilter(Kind:InventoryEntryKind.ExcludedReparse),
            "unavailable"=>new IndexedInventoryFilter(Metadata:IndexedInventoryMetadataMode.Unavailable),
            "literal"=>new IndexedInventoryFilter(Text:"%_"),
            _=>new IndexedInventoryFilter(Text:"newname",Kind:InventoryEntryKind.File,MinBytes:7,MaxBytes:7,
                ModifiedFrom:day,ModifiedThrough:day,Metadata:IndexedInventoryMetadataMode.Available,Extension:".txt")};
        var view=await Query(f).OpenAsync(Select(f,filter:filter));Assert.Equal(count,view.FilteredCounts.Total);
        Assert.Equal(6,view.TotalCounts.Total);Assert.Equal(count,view.ReadPage().Rows.Count);
        if(scenario is "kind" or "unavailable")Assert.Equal(1,view.FilteredCounts.Unknown);
    }
    [Fact]
    public async Task PredicatesCannotMixOldNameWithNewSizeAndClassificationIsAnded()
    {
        var paths=new AppDataPaths(new TestWorkspace().Root);var a=await Commit(paths,Truth(Entry("old.txt","id",1)));
        var b=await Commit(paths,Truth(Entry("new.txt","id",10)),a);var f=new Fixture(paths,a,b);
        var mixed=await Query(f).OpenAsync(Select(f,filter:new(Text:"old",MinBytes:5)));Assert.Empty(mixed.ReadPage().Rows);
        var matched=await Query(f).OpenAsync(Select(f,InventoryDifferenceKind.Added,new(Text:"new")));
        Assert.Empty(matched.ReadPage().Rows);Assert.Equal(1,matched.TotalCounts.Renamed);
    }
    [Theory]
    [InlineData(SuppliedInventoryCoverage.Unknown)] [InlineData(SuppliedInventoryCoverage.Offline)]
    public async Task UnknownCoverageRemainsUnknownInPagesAndBothCountSets(SuppliedInventoryCoverage coverage)
    {
        var f=await Seed();var selection=Select(f) with {Snapshots=Select(f).Snapshots with {AfterCoverage=coverage}};
        var view=await Query(f).OpenAsync(selection);Assert.Equal(8,view.TotalCounts.Unknown);
        Assert.Equal(0,view.TotalCounts.Removed);Assert.Equal(0,view.TotalCounts.Renamed);
        Assert.All(view.ReadPage().Rows,r=>Assert.Equal(InventoryDifferenceKind.Unknown,r.Kind));
        var removed=await Query(f).OpenAsync(selection with {Classification=InventoryDifferenceKind.Removed});
        Assert.Equal(8,removed.TotalCounts.Unknown);Assert.Equal(0,removed.FilteredCounts.Total);Assert.Empty(removed.ReadPage().Rows);
    }
    [Fact]
    public async Task UnprovedIdentityCannotBecomeKnownThroughSavedFilters()
    {
        var f=await Seed(InventoryIdentityEvidence.Unverified);var view=await Query(f).OpenAsync(Select(f,filter:new(Extension:".txt")));
        Assert.Equal(8,view.TotalCounts.Unknown);Assert.True(view.FilteredCounts.Unknown>0);
        Assert.Equal(view.FilteredCounts.Total,view.FilteredCounts.Unknown);Assert.Equal(0,view.FilteredCounts.Removed);
    }
    [Fact]
    public async Task SelfComparisonIsEmptyWithoutIssuingFilteredReads()
    {
        var f=await Seed();var view=await Query(f,new FaultReader("unexpected-read")).OpenAsync(Select(f,filter:new(Text:"txt")) with
            {Snapshots=Select(f).Snapshots with {AfterGeneration=f.Before.Root.GenerationId}});
        Assert.Empty(view.ReadPage().Rows);Assert.Equal(0,view.TotalCounts.Total);
    }
    private sealed class FaultComparer(string code):IInventoryGenerationComparer
    {
        public int Calls {get;private set;}
        public Task<InventoryComparisonResult> CompareAsync(InventoryComparisonRequest request,CancellationToken cancellationToken=default)
        {Calls++;throw new InventoryIndexException(code);}
    }
    private sealed class FaultReader(string code):IIndexedInventoryReader
    {
        public Task<IndexedCatalogPage> ReadCatalogsAsync(IndexedCatalogRequest request,CancellationToken token)=>throw new InvalidOperationException("unexpected-catalog");
        public Task<IndexedInventoryPage> ReadPageAsync(IndexedInventoryRequest request,CancellationToken token)=>throw new InventoryIndexException(code);
    }
    [Theory]
    [InlineData("inventory.comparison_incompatible")] [InlineData("inventory.comparison_incomplete")]
    [InlineData("inventory.comparison_uncommitted")] [InlineData("inventory.comparison_schema_unsupported")]
    public async Task ComparerRefusalsPropagateExactlyRatherThanProducingEmptyViews(string code)
    {
        var query=new InventorySnapshotComparisons(new FaultComparer(code),new FaultReader("unexpected-read"));
        var selection=new SnapshotComparisonSelection(new("snapshot-fixture",Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")));
        Assert.Equal(code,(await Assert.ThrowsAsync<InventoryIndexException>(()=>query.OpenAsync(selection))).Code);
    }
    [Fact]
    public async Task ActualIncompleteWrongRootAndMissingGenerationAreRefused()
    {
        var f=await Seed();var query=Query(f);
        Assert.Equal("inventory.comparison_incompatible",(await Assert.ThrowsAsync<InventoryIndexException>(()=>query.OpenAsync(Select(f) with
            {Snapshots=Select(f).Snapshots with {RootIdentity="other-root"}}))).Code);
        Assert.Equal("inventory.comparison_uncommitted",(await Assert.ThrowsAsync<InventoryIndexException>(()=>query.OpenAsync(Select(f) with
            {Snapshots=Select(f).Snapshots with {BeforeGeneration=Guid.NewGuid().ToString("N")}}))).Code);
        Execute(f.Paths,"UPDATE InventoryScan SET Status='Incomplete' WHERE GenerationId=(SELECT GenerationId FROM InventoryPublishedRoot);");
        Assert.Equal("inventory.comparison_incomplete",(await Assert.ThrowsAsync<InventoryIndexException>(()=>query.OpenAsync(Select(f)))).Code);
    }
    [Fact]
    public async Task ActualPreV5WithoutWitnessIsRefusedAndNeverMigrated()
    {
        var paths=new AppDataPaths(new TestWorkspace().Root);await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var store=new SqliteInventoryStore(paths);var raw=await store.IndexAsync(Truth(Entry("raw","raw")),1,CancellationToken.None);
        var query=new InventorySnapshotComparisons(store,new SqliteIndexedInventoryReader(paths));
        Assert.Equal("inventory.comparison_schema_unsupported",(await Assert.ThrowsAsync<InventoryIndexException>(()=>query.OpenAsync(
            new(new("snapshot-fixture",raw.GenerationId,raw.GenerationId))))).Code);
        using var c=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=paths.DatabasePath,Pooling=false}.ToString());c.Open();
        using var cmd=c.CreateCommand();cmd.CommandText="PRAGMA user_version;";Assert.Equal(2L,cmd.ExecuteScalar());
    }
    [Theory]
    [InlineData("inventory.query_time_limit")] [InlineData("inventory.query_scan_limit")] [InlineData("inventory.query_unavailable")]
    public async Task SavedQueryFailuresAreExplicitAndNeverPartialSuccess(string code)
    {
        var f=await Seed();Assert.Equal(code,(await Assert.ThrowsAsync<InventoryIndexException>(()=>Query(f,new FaultReader(code))
            .OpenAsync(Select(f,filter:new(Text:"txt"))))).Code);
    }
    [Fact]
    public async Task ReadOnlyProofCoversDatabaseBackupsHistoryCheckpointAndFloor()
    {
        var f=await Seed();var files=Directory.GetFiles(f.Paths.DataDirectory,"*",SearchOption.AllDirectories).Order().ToArray();
        var hashes=files.Select(p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        var before=await new SqliteInventoryStore(f.Paths).ReadReconciliationAsync("snapshot-fixture",CancellationToken.None);
        var view=await Query(f).OpenAsync(Select(f,filter:new(Extension:".txt")));_ = view.ReadPage(2);_ = view.ReadPage(2,view.ReadPage(2).NextCursor);
        // Existing read-only SQLite readers may create only their WAL/SHM coordination sidecars.
        // Preserve the stronger byte proof for every original DB/backup and refuse any other addition.
        var afterFiles=Directory.GetFiles(f.Paths.DataDirectory,"*",SearchOption.AllDirectories).Order().ToArray();
        Assert.All(files,p=>Assert.Contains(p,afterFiles));
        Assert.All(afterFiles.Except(files),p=>Assert.True(p==f.Paths.DatabasePath+"-wal" || p==f.Paths.DatabasePath+"-shm"));
        Assert.Equal(hashes,files.Select(p=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))).ToArray());
        var after=await new SqliteInventoryStore(f.Paths).ReadReconciliationAsync("snapshot-fixture",CancellationToken.None);
        Assert.Equal(before,after);Assert.Equal(before!.Floor,after!.Floor);
    }
    [Fact]
    public async Task LaterPublicationCannotChangeOpenedViewOrPinnedReopenedHistoricalSelection()
    {
        var f=await Seed();var query=Query(f);var view=await query.OpenAsync(Select(f,filter:new(Extension:".txt")));
        var original=JsonSerializer.Serialize(view.ReadPage(2));
        _=await Commit(f.Paths,Truth(Entry("third","third")),f.After);
        Assert.Equal(original,JsonSerializer.Serialize(view.ReadPage(2)));
        var reopened=await query.OpenAsync(Select(f,filter:new(Extension:".txt")));Assert.Equal(view.ViewIdentity,reopened.ViewIdentity);
        Assert.Equal(original,JsonSerializer.Serialize(reopened.ReadPage(2)));
    }
    [Theory]
    [InlineData(0)] [InlineData(201)]
    public async Task InvalidPageSizeRefuses(int size)
    {var f=await Seed();var view=await Query(f).OpenAsync(Select(f));Assert.Equal("inventory.snapshot_invalid_page",Assert.Throws<InventoryIndexException>(()=>view.ReadPage(size)).Code);}
    [Fact]
    public async Task InvalidCursorOffsetsAndIdentityRefuse()
    {
        var f=await Seed();var view=await Query(f).OpenAsync(Select(f));var cursor=view.ReadPage(2).NextCursor!;
        foreach(var bad in new[]{cursor with {NextIndex=0},cursor with {NextIndex=3},cursor with {NextIndex=100},cursor with {ViewIdentity="other"}})
            Assert.Throws<InventoryIndexException>(()=>view.ReadPage(2,bad));
    }
    [Fact]
    public async Task InvalidFiltersRefuseBeforeComparisonAndNullFilterNormalizesDeterministically()
    {
        var f=await Seed();var comparer=new FaultComparer("should-not-run");var query=new InventorySnapshotComparisons(comparer,new FaultReader("should-not-run"));
        await Assert.ThrowsAsync<InventoryIndexException>(()=>query.OpenAsync(Select(f,(InventoryDifferenceKind)99)));
        await Assert.ThrowsAsync<InventoryIndexException>(()=>query.OpenAsync(Select(f,filter:new(MinBytes:-1))));Assert.Equal(0,comparer.Calls);
        Assert.Equal((await Query(f).OpenAsync(Select(f))).ViewIdentity,(await Query(f).OpenAsync(Select(f,filter:new()))).ViewIdentity);
    }
    [Fact]
    public async Task CancellationDoesNotBecomeAnEmptyViewOrPage()
    {
        var f=await Seed();using var c=new CancellationTokenSource();c.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Query(f).OpenAsync(Select(f),c.Token));
        var view=await Query(f).OpenAsync(Select(f));Assert.ThrowsAny<OperationCanceledException>(()=>view.ReadPage(cancellationToken:c.Token));
    }

    [Fact]
    public async Task FilteredEndpointQueriesCrossTheirExistingTwoHundredRowBoundary()
    {
        var paths=new AppDataPaths(new TestWorkspace().Root);
        var a=await Commit(paths,Truth(Enumerable.Range(0,240).Select(i=>Entry($"f{i:000}.txt",$"id{i:000}",1)).ToArray()));
        var b=await Commit(paths,Truth(Enumerable.Range(0,240).Select(i=>Entry($"f{i:000}.txt",$"id{i:000}",2)).ToArray()),a);
        var f=new Fixture(paths,a,b);var view=await Query(f).OpenAsync(Select(f,InventoryDifferenceKind.Changed,new(MinBytes:2,Extension:".txt")));
        Assert.Equal(240,view.TotalCounts.Changed);Assert.Equal(240,view.FilteredCounts.Changed);
        SnapshotComparisonCursor? cursor=null;var rows=new List<InventoryDifference>();
        do {var page=view.ReadPage(50,cursor);rows.AddRange(page.Rows);cursor=page.NextCursor;}while(cursor is not null);
        Assert.Equal(240,rows.Distinct().Count());Assert.Equal("f000.txt",rows[0].AfterPath);Assert.Equal("f239.txt",rows[^1].AfterPath);
    }
    private sealed class WrappedReader(IIndexedInventoryReader inner,Func<int,IndexedInventoryPage,IndexedInventoryPage> transform):IIndexedInventoryReader
    {
        private int calls;
        public Task<IndexedCatalogPage> ReadCatalogsAsync(IndexedCatalogRequest request,CancellationToken token)=>throw new InvalidOperationException("unexpected-catalog");
        public async Task<IndexedInventoryPage> ReadPageAsync(IndexedInventoryRequest request,CancellationToken token)
            =>transform(++calls,await inner.ReadPageAsync(request,token));
    }
    [Fact]
    public async Task FailureOnSecondEndpointCannotExposeTheAlreadyEvaluatedFirstHalf()
    {
        var f=await Seed();var reader=new WrappedReader(new SqliteIndexedInventoryReader(f.Paths),(n,page)=>
            n==2?throw new InventoryIndexException("inventory.query_time_limit"):page);
        Assert.Equal("inventory.query_time_limit",(await Assert.ThrowsAsync<InventoryIndexException>(()=>Query(f,reader)
            .OpenAsync(Select(f,filter:new(Extension:".txt"))))).Code);
    }
    [Fact]
    public async Task EndpointReaderCannotSubstituteAnotherGeneration()
    {
        var f=await Seed();var reader=new WrappedReader(new SqliteIndexedInventoryReader(f.Paths),(n,page)=>
            page with {Catalog=page.Catalog with {GenerationId=Guid.NewGuid().ToString("N")}});
        Assert.Equal("inventory.snapshot_selection_mismatch",(await Assert.ThrowsAsync<InventoryIndexException>(()=>Query(f,reader)
            .OpenAsync(Select(f,filter:new(Extension:".txt"))))).Code);
    }
}
