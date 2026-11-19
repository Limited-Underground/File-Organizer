using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class IndexedInventoryMetadataBenchmark
{
    [IndexedMetadataBenchmarkFact]
    public async Task ExistingMillionRowCopyMatchesCombinedMetadataOraclesWithoutMutation()
    {
        var task = Path.Combine(ProcessInventorySession.FindCheckout(), "artifacts", "FO-012-filters-20261101");
        RejectReparseAncestors(task);
        using var prepared = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(task, "smoke-large-profile.json")));
        var supplied = prepared.RootElement.GetProperty("profile").GetString()!;
        var id = Path.GetFileName(supplied);
        Assert.True(Guid.TryParseExact(id, "N", out _));
        Assert.Equal(id.ToLowerInvariant(), id);
        var profile = Path.Combine(task, "smoke", id);
        Assert.Equal(Path.GetFullPath(profile), Path.GetFullPath(supplied), ignoreCase: true);
        RejectReparseAncestors(profile);
        using var owner = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(profile, ".indexed-ui-profile-owner.json")));
        Assert.Equal(1, owner.RootElement.GetProperty("FormatVersion").GetInt32());
        Assert.Equal(id, owner.RootElement.GetProperty("Id").GetString());
        Assert.Equal("FO-012-filters-20261101", owner.RootElement.GetProperty("Task").GetString());
        var paths = new AppDataPaths(profile);
        Assert.Equal(paths.DatabasePath, prepared.RootElement.GetProperty("database").GetString(), ignoreCase: true);
        Assert.True(prepared.RootElement.GetProperty("sourceUnchanged").GetBoolean());
        Assert.Equal(1_000_000, prepared.RootElement.GetProperty("counts").GetProperty("entries").GetInt32());
        Assert.Equal(100, prepared.RootElement.GetProperty("counts").GetProperty("roots").GetInt32());
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var elapsed = Stopwatch.StartNew();
        var initialHash = await HashDatabase(paths, deadline.Token);
        var reader = new SqliteIndexedInventoryReader(paths);
        var catalogs = new List<IndexedCatalog>();
        string? afterRoot = null;
        do
        {
            var page = await reader.ReadCatalogsAsync(new(50, afterRoot), deadline.Token);
            catalogs.AddRange(page.Catalogs);
            Assert.True(catalogs.Count <= 100);
            afterRoot = page.NextAfterRootIdentity;
        } while (afterRoot is not null);
        Assert.Equal(100, catalogs.Count);
        var measurements = new List<object>();
        var outcome = "Failed";
        string? error = null;
        try
        {
            foreach (var root in new[] { 0, 9, 50, 99 })
            {
                var rootIdentity = $"capacity-r{root:D3}-{(root < 10 ? "offline" : "online")}";
                var catalog = Assert.Single(catalogs, value => value.RootIdentity == rootIdentity);
                Assert.NotNull(catalog.GenerationId);
                foreach (var filter in Cases())
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var expected = ReferenceRows(root).Where(row => OracleMatches(row, filter))
                        .Select(row => row.Path).Order(StringComparer.Ordinal).ToArray();
                    var request = new IndexedInventoryRequest(rootIdentity, catalog.GenerationId!, 50, Filter: filter);
                    _ = await reader.ReadPageAsync(request, deadline.Token); // one warmup
                    var samples = new double[3];
                    for (var sample = 0; sample < samples.Length; sample++)
                    {
                        var timer = Stopwatch.StartNew();
                        var first = await reader.ReadPageAsync(request, deadline.Token);
                        samples[sample] = timer.Elapsed.TotalMilliseconds;
                        Assert.Equal(expected.Take(50), first.Rows.Select(row => row.RelativePath));
                    }
                    var firstHash = await VerifyPages(reader, request, expected, deadline.Token);
                    var repeatedHash = await VerifyPages(reader, request, expected, deadline.Token);
                    Assert.Equal(firstHash, repeatedHash);
                    measurements.Add(new { Root = root, filter.Text, filter.Scope, filter.Kind, filter.MinBytes, filter.MaxBytes, filter.ModifiedFrom, filter.ModifiedThrough, filter.CaseMode, filter.Metadata,
                        ExpectedRows = expected.Length, OrderedPathSha256 = firstHash, FirstPageMilliseconds = samples });
                }
            }
            Assert.Equal(136, measurements.Count);
            Assert.Equal(initialHash, await HashDatabase(paths, deadline.Token));
            outcome = "Passed";
        }
        catch (Exception exception)
        {
            error = exception is InventoryIndexException index ? index.Code : exception.GetType().Name;
            throw;
        }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(task, "filter-query-measurement.json"), JsonSerializer.Serialize(new
            {
                Outcome = outcome, ErrorCode = error, CasesCompleted = measurements.Count, Measurements = measurements,
                ElapsedMilliseconds = elapsed.Elapsed.TotalMilliseconds, SourceDatabaseSha256 = initialHash,
                PeakProcessWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64,
                PageSize = 50, WarmupsPerCase = 1, TimedFirstPagesPerCase = 3,
                Protocol = "Four roots (0,9,50,99), 22 unchanged text/kind cases plus12 metadata/case cases each; two complete ordered oracle paging passes per case",
                Limits = "Read-only existing disposable million-row copy; no ingestion, index changes, latency threshold or global search qualification. Native WAL/SHM coordination may occur. Three-minute cooperative run budget; each filtered provider call retains production limits.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static IEnumerable<IndexedInventoryFilter> Cases()
    {
        foreach (var scope in new[] { IndexedInventorySearchScope.Name, IndexedInventorySearchScope.Path })
        foreach (var text in scope == IndexedInventorySearchScope.Name ? new[] { "item", "item-0000.", "!not-present!" } : new[] { "d0", "d037/item-0000.", "!not-present!" })
        foreach (var kind in new InventoryEntryKind?[] { null, InventoryEntryKind.File, InventoryEntryKind.Directory })
            yield return new(text, scope, kind);
        foreach (var kind in new InventoryEntryKind?[] { null, InventoryEntryKind.File, InventoryEntryKind.Directory, InventoryEntryKind.ExcludedReparse })
            yield return new(Kind: kind);
        yield return new(MinBytes: 0, MaxBytes: 0);
        yield return new(MinBytes: 4095, MaxBytes: 4097);
        yield return new(MinBytes: 1048576);
        yield return new(Kind: InventoryEntryKind.Directory, MaxBytes: 0);
        yield return new(Metadata: IndexedInventoryMetadataMode.Unavailable);
        yield return new(ModifiedFrom: new(2020,2,29), ModifiedThrough: new(2020,2,29));
        yield return new(ModifiedFrom: new(2021,1,1));
        yield return new("d050/", IndexedInventorySearchScope.Path, InventoryEntryKind.File,
            1, 65535, new(2020,1,1), new(2020,12,31), Metadata: IndexedInventoryMetadataMode.Available);
        yield return new("ITEM-0000.", Kind: InventoryEntryKind.File, MinBytes: 0, CaseMode: IndexedInventoryCaseMode.MatchCase);
        yield return new("ITEM-0000.", Kind: InventoryEntryKind.File, MinBytes: 0, CaseMode: IndexedInventoryCaseMode.IgnoreCase);
        yield return new("%_\\'", MinBytes: 0, ModifiedThrough: DateOnly.MaxValue);
        yield return new("d037/", IndexedInventorySearchScope.Path, ModifiedFrom: new(2020,1,1),
            ModifiedThrough: new(2020,6,30), Metadata: IndexedInventoryMetadataMode.Available);
    }

    private static bool OracleMatches((string Path, InventoryEntryKind Kind, long Length, DateTime Modified) row, IndexedInventoryFilter filter)
    {
        // This fixture contains only known file/folder observations. Availability-no-hit is intentional.
        var day = DateOnly.FromDateTime(row.Modified);
        return (filter.Kind is null || row.Kind == filter.Kind) && filter.Metadata != IndexedInventoryMetadataMode.Unavailable &&
            ((filter.MinBytes is null && filter.MaxBytes is null) || (row.Kind == InventoryEntryKind.File &&
                (filter.MinBytes is null || row.Length >= filter.MinBytes) && (filter.MaxBytes is null || row.Length <= filter.MaxBytes))) &&
            (filter.ModifiedFrom is null || day >= filter.ModifiedFrom) &&
            (filter.ModifiedThrough is null || day <= filter.ModifiedThrough) &&
            (filter.Scope == IndexedInventorySearchScope.Name ? row.Path[(row.Path.LastIndexOf('/')+1)..] : row.Path)
                .Contains(filter.Text, filter.CaseMode == IndexedInventoryCaseMode.MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
    }

    // Independent arithmetic path oracle; does not call the source generator or query SQL.
    private static IEnumerable<(string Path, InventoryEntryKind Kind, long Length, DateTime Modified)> ReferenceRows(int root)
    {
        for (var directory = 0; directory < 100; directory++)
            yield return ($"d{directory:D3}", InventoryEntryKind.Directory, 0L, new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc));
        string[] extensions = ["txt", "bin", "jpg", "log", "dat"];
        for (var ordinal = 0; ordinal < 9900; ordinal++)
        {
            var physical = ordinal < 9800 ? ordinal : ordinal - 9800;
            var extension = extensions[(104729 + root * 9800 + physical) % 5];
            var name = ordinal < 9800 ? "item-" + (ordinal / 100).ToString("D4", CultureInfo.InvariantCulture) : "alias-" + physical.ToString("D4", CultureInfo.InvariantCulture);
            var number = (long)root * 9800 + physical;
            var size = (number + 104729) % 17;
            long length = size switch { 0=>0L, 1=>1L, 2=>4095L, 3=>4096L, 4=>4097L, 5=>1048576L,
                _=> ((long)root * 73856093 + (long)physical * 19349663 + 104729) % 65536 };
            yield return ($"d{ordinal % 100:D3}/{name}.{extension}", InventoryEntryKind.File, length,
                new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc).AddSeconds(number * 17 % 31536000));
        }
    }

    private static async Task<string> VerifyPages(IIndexedInventoryReader reader, IndexedInventoryRequest initial, string[] expected, CancellationToken token)
    {
        IndexedInventoryCursor? after = null;
        var index = 0;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        do
        {
            var page = await reader.ReadPageAsync(initial with { After = after }, token);
            Assert.True(page.IsCurrentPublication);
            Assert.Equal(initial.GenerationId, page.Catalog.GenerationId);
            foreach (var row in page.Rows)
            {
                Assert.True(index < expected.Length);
                Assert.Equal(expected[index++], row.RelativePath);
                digest.AppendData(System.Text.Encoding.UTF8.GetBytes(row.RelativePath + "\n"));
            }
            Assert.True(index <= expected.Length);
            Assert.True(page.NextCursor is null || page.NextCursor != after);
            after = page.NextCursor;
        } while (after is not null);
        Assert.Equal(expected.Length, index);
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static async Task<string> HashDatabase(AppDataPaths paths, CancellationToken token)
    {
        using var stream = File.OpenRead(paths.DatabasePath);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token));
    }

    private static void RejectReparseAncestors(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            Assert.False(current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint));
    }
}

public sealed class IndexedMetadataBenchmarkFactAttribute : FactAttribute
{
    public IndexedMetadataBenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FO_RUN_INDEX_FILTERS") != "1")
            Skip = "Existing-copy search benchmark requires explicit FO_RUN_INDEX_FILTERS=1; no indexing is performed.";
        else if (typeof(IndexedInventoryMetadataBenchmark).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration != "Release")
            Skip = "The existing-copy search benchmark requires the reviewed Release build.";
    }
}
