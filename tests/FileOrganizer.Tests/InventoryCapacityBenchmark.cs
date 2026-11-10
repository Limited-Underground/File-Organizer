using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FileOrganizer.Application;
using FileOrganizer.Persistence;
using FileOrganizer.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class InventoryCapacityBenchmark
{
    private const int RowsPerRoot = 10_000;
    private const int PageSize = 200;
    private static readonly DateTime Epoch = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Families = ["children", "name-prefix", "size-order", "size-filter", "time-filter", "type-filter", "root-filter", "offline-filter", "duplicate-size"];

    [Fact]
    public void ExactQueryReceiptSerializationKeepsRootAndQueryOfflineDistinctAndRejectsMixedFamilies()
    {
        var query = QueryCase.Create("offline-filter", 21);
        Assert.True(query.Root < 10);
        Assert.False(query.Offline);
        var receipt = CreateQueryReceipt(query, 0, "fixture-ordered-hash", [1, 2, 3, 4, 5], ["fixture-plan"]);
        var bytes = SerializeQueryFamily("offline-filter", [receipt]);
        using var document = JsonDocument.Parse(bytes);
        var row = Assert.Single(document.RootElement.EnumerateArray());
        Assert.Equal("offline-filter", row.GetProperty("family").GetString());
        Assert.True(row.GetProperty("rootOffline").GetBoolean());
        Assert.False(row.GetProperty("queryOffline").GetBoolean());
        Assert.Equal(0, row.GetProperty("expectedRows").GetInt32());
        var propertyNames = row.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(propertyNames.Length, propertyNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var roundTrip = JsonSerializer.Deserialize<QueryReceipt[]>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(roundTrip);
        Assert.Equal(receipt.RootOffline, Assert.Single(roundTrip).RootOffline);
        Assert.Equal(receipt.QueryOffline, Assert.Single(roundTrip).QueryOffline);
        Assert.Throws<InvalidOperationException>(() => SerializeQueryFamily("children", [receipt]));
        var childReceipt = CreateQueryReceipt(QueryCase.Create("children", 0), 100, "child-hash", [1, 2, 3, 4, 5], []);
        Assert.Throws<InvalidOperationException>(() => SerializeQueryFamily("offline-filter", [receipt, childReceipt]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(9)]
    [InlineData(99)]
    public async Task ArithmeticPagedSourceMatchesIndependentReferenceAndAliasMetadata(int root)
    {
        var source = new ArithmeticInventorySource(root);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CaptureAsync(CancellationToken.None));
        await using var lease = await source.OpenReadLeaseAsync(CancellationToken.None);
        Assert.Equal(RootIdentity(root), lease.RootIdentity);
        await using (var reader = await lease.OpenDirectoryAsync(string.Empty, lease.RootIdentity, CancellationToken.None))
        {
            var seen = 0;
            while (true)
            {
                var page = await reader.ReadPageAsync(17, CancellationToken.None);
                foreach (var entry in page.Entries) Assert.Equal(ReferenceRow(root * RowsPerRoot + seen++), entry);
                if (page.EndOfDirectory) break;
            }
            Assert.Equal(100, seen);
        }
        foreach (var directory in new[] { 0, 37, 99 })
        {
            var directoryEntry = ReferenceRow(root * RowsPerRoot + directory);
            await using var reader = await lease.OpenDirectoryAsync(directoryEntry.RelativePath, directoryEntry.ObjectIdentity, CancellationToken.None);
            var seen = 0;
            while (true)
            {
                var page = await reader.ReadPageAsync(31, CancellationToken.None);
                foreach (var entry in page.Entries)
                {
                    Assert.Equal(ReferenceRow(root * RowsPerRoot + 100 + seen * 100 + directory), entry);
                    seen++;
                }
                if (page.EndOfDirectory) break;
            }
            Assert.Equal(99, seen);
        }
    }

    [CapacityBenchmarkFact]
    public async Task OneMillionMetadataEntriesThroughCapturePipelineAndExactPagedOracles()
    {
        using var run = CapacityRun.Create();
        var receipts = new List<QueryReceipt>();
        var phases = new List<object>();
        var stopwatch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(45));
        var outcome = "Failed";
        string? failure = null;
        string? sqliteVersion = null;
        try
        {
            run.Check(stopwatch);
            var manifest = CreateManifest(timeout.Token, run, stopwatch);
            run.WriteJson("dataset-manifest.json", manifest);
            var options = new InventoryCaptureOptions
            {
                PageSize = 1000, MaxEntries = 1_100_000, MaxDirectories = 12_000, MaxDepth = 32,
                MaxDuration = TimeSpan.FromMinutes(45), MaxDatabaseBytes = 2L << 30,
                MaxWalBytes = 512L << 20, MaxResidentBytes = 1L << 30,
                MaxRunBytes = 6L << 30, MinimumFreeBytes = 12L << 30,
            };
            using var profileLease = ProfileLease.Acquire(run.Paths);
            await new SqliteApplicationStore(run.Paths).InitializeAsync(timeout.Token);
            using (var admission = Open(run.Paths.DatabasePath))
            using (var configuration = admission.CreateCommand())
            {
                configuration.CommandText = "PRAGMA temp_store=MEMORY; PRAGMA hard_heap_limit=268435456;";
                configuration.ExecuteNonQuery();
                Assert.InRange(Convert.ToInt64(Scalar(admission, "PRAGMA hard_heap_limit;"), CultureInfo.InvariantCulture), 1L, 256L << 20);
            }
            var store = new SqliteInventoryStore(run.Paths);
            var progress = new BudgetProgress(run, stopwatch);
            var loadTimer = Stopwatch.StartNew();
            for (var root = 0; root < 100; root++)
            {
                run.Check(stopwatch);
                var source = new ArithmeticInventorySource(root);
                InventoryIndexResult result;
                if (root == 0)
                {
                    using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                    string? generation = null;
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CaptureAsync(source, options, null, interrupted.Token,
                        new BudgetProgress(run, stopwatch, value =>
                        {
                            generation = value.GenerationId;
                            if (value.Phase == InventoryCapturePhase.Capturing && value.CapturedEntries >= 1000) interrupted.Cancel();
                        })));
                    Assert.False(string.IsNullOrEmpty(generation));
                    result = await new SqliteInventoryStore(run.Paths).CaptureAsync(source, options, generation, timeout.Token, progress);
                    Assert.Equal(generation, result.GenerationId);
                }
                else result = await store.CaptureAsync(source, options, null, timeout.Token, progress);
                Assert.True(result.Published);
                Assert.Equal(InventoryRunStatus.Completed, result.Status);
                Assert.Equal(RowsPerRoot, result.CommittedEntries);
                Assert.Equal(RowsPerRoot, result.TotalEntries);
                Assert.Equal(manifest.Roots[root].CaptureFingerprint, result.Fingerprint);
                using var footprint = Open(run.Paths.DatabasePath);
                run.WriteJson($"root-{root:D3}.json", new { root, source.RootIdentity, source.IsOffline, result,
                    elapsedSeconds = loadTimer.Elapsed.TotalSeconds, resources = run.Observe(),
                    entryRows = Scalar(footprint, "SELECT count(*) FROM InventoryDirectoryEntry;"),
                    sqlitePages = Scalar(footprint, "PRAGMA page_count;"), sqlitePageSize = Scalar(footprint, "PRAGMA page_size;") });
            }
            phases.Add(new { phase = "capture-100-roots", elapsedSeconds = loadTimer.Elapsed.TotalSeconds });
            run.Check(stopwatch);
            using var connection = Open(run.Paths.DatabasePath);
            sqliteVersion = Convert.ToString(Scalar(connection, "SELECT sqlite_version();"), CultureInfo.InvariantCulture);
            using (var setup = connection.CreateCommand())
            {
                setup.CommandText = "PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY; PRAGMA hard_heap_limit=268435456;";
                setup.ExecuteNonQuery();
            }
            VerifyDataset(connection, manifest, timeout.Token, run, stopwatch);
            timeout.Token.ThrowIfCancellationRequested(); run.Check(stopwatch);
            var analyzeWatch = Stopwatch.StartNew();
            using (var analyze = connection.CreateCommand())
            {
                analyze.CommandText = "ANALYZE;";
                analyze.CommandTimeout = 120;
                analyze.ExecuteNonQuery();
            }
            analyzeWatch.Stop();
            timeout.Token.ThrowIfCancellationRequested(); run.Check(stopwatch);
            phases.Add(new { phase = "analyze-owned-database", elapsedSeconds = analyzeWatch.Elapsed.TotalSeconds });
            run.WriteJson("analyze.json", new { outcome = "Completed", elapsedMilliseconds = analyzeWatch.Elapsed.TotalMilliseconds,
                scope = "This minted capacity database only; no schema/index/cap changes.", queryStatistics = Scalar(connection, "SELECT count(*) FROM sqlite_stat1;") });
            VerifyTimedAggregates(connection, manifest, timeout.Token, run, stopwatch);
            run.WriteJson("schema-and-indices.json", ReadSchema(connection, timeout.Token));
            var queryTimer = Stopwatch.StartNew();
            foreach (var family in Families)
            {
                var familySamples = new List<double>(500);
                var familyReceipts = new List<QueryReceipt>(100);
                for (var number = 0; number < 100; number++)
                {
                    run.Check(stopwatch);
                    timeout.Token.ThrowIfCancellationRequested();
                    var query = QueryCase.Create(family, number);
                    var expected = ExpectedIds(query, timeout.Token);
                    var generation = Generation(connection, query.Root);
                    var plan = Explain(connection, query, generation, timeout.Token);
                    _ = ReadPage(connection, query, generation, null, timeout.Token); // one warmup
                    var samples = new double[5];
                    for (var sample = 0; sample < samples.Length; sample++)
                    {
                        var watch = Stopwatch.StartNew();
                        var page = ReadPage(connection, query, generation, null, timeout.Token);
                        samples[sample] = watch.Elapsed.TotalMilliseconds;
                        familySamples.Add(samples[sample]);
                        Assert.Equal(expected.Take(PageSize), page.Ids);
                        run.Check(stopwatch);
                    }
                    var hash = VerifyAllPages(connection, query, generation, expected, timeout.Token, run, stopwatch);
                    var repeated = VerifyAllPages(connection, query, generation, expected, timeout.Token, run, stopwatch);
                    Assert.Equal(hash, repeated);
                    if (family == "duplicate-size") VerifyDuplicateGroup(connection, query, generation, timeout.Token);
                    var receipt = CreateQueryReceipt(query, expected.Length, hash, samples, plan);
                    receipts.Add(receipt);
                    familyReceipts.Add(receipt);
                }
                run.WriteQueryFamily(family, familyReceipts);
                var sorted = familySamples.Order().ToArray();
                run.WriteJson($"timings-{family}.json", new
                {
                    family, seededInputs = 100, warmupsPerInput = 1, measurementsPerInput = 5,
                    measuredFirstPages = sorted.Length, pageSize = PageSize, minimumMilliseconds = sorted[0],
                    medianMilliseconds = sorted[(sorted.Length - 1) / 2],
                    p95Milliseconds = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1], maximumMilliseconds = sorted[^1],
                    proposedP95ThresholdMilliseconds = 250,
                    measuredP95WithinProposal = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1] <= 250,
                    referenceHostAcceptance = "Not established; measured query+validation overhead is recorded, not owner performance acceptance. No OS cache clearing.",
                });
            }
            phases.Add(new { phase = "900-query-cases", elapsedSeconds = queryTimer.Elapsed.TotalSeconds });
            Assert.Equal(900, receipts.Count);
            run.Check(stopwatch);
            VerifyOnlineBackup(connection, run, timeout.Token, stopwatch);
            VerifyIntegrity(connection, timeout.Token);
            run.Check(stopwatch);
            outcome = "CorrectnessPassed";
        }
        catch (Exception exception)
        {
            failure = exception is InventoryIndexException index ? index.Code : exception.GetType().Name;
            throw;
        }
        finally
        {
            run.WriteJson("capacity-result.json", new
            {
                outcome, failure, totalSeconds = stopwatch.Elapsed.TotalSeconds, seed = 104729,
                performanceAcceptance = "Requires reference-host and owner review; query timing proposals are reported separately from exact correctness.",
                logicalRoots = 100, explicitlyOfflineRoots = 10, directoryEntries = 1_000_000,
                distinctObjects = 990_000, distinctFileObjects = 980_000, hardLinkAliases = 10_000,
                queryFamilies = Families, acceptedQueryCases = receipts.Count, phases,
                environment = new { os = Environment.OSVersion.VersionString, runtime = Environment.Version.ToString(), Environment.ProcessorCount,
                    processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                    availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, debuggerAttached = Debugger.IsAttached,
                    configuration = typeof(InventoryCapacityBenchmark).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                        .Cast<System.Reflection.AssemblyConfigurationAttribute>().SingleOrDefault()?.Configuration,
                    sourceCommit = ReadHeadIdentity(), sourceIdentityLimit = "HEAD alone excludes reviewed uncommitted changes; the owning run source manifest must identify exact candidate bytes.", sqliteVersion },
                budgets = new { mainDbBytes = 2L << 30, walBytes = 512L << 20, tempBytes = 256L << 20,
                    backupBytes = 2L << 30, receiptBytes = 64L << 20, runBytes = 6L << 30, rssBytes = 1L << 30,
                    minimumFreeBytes = 12L << 30, minutes = 45 },
                limitations = "Metadata-only capacity through the shared capture pipeline, not a million-file scan, hardware test, power-loss test, or owner acceptance of performance. SQLite/temp calls remain cooperatively bounded; no OS cache clearing. Offline is explicit catalogue metadata in synthetic root IDs, not media disconnection.",
                resources = run.Observe(),
            });
        }
    }

    // Reference arithmetic is deliberately separate from ArithmeticInventorySource implementation.
    // IDs 0..99 are directory objects; IDs 100..9999 are file names including the final 100 aliases.
    private static InventoryEntry ReferenceRow(int id)
    {
        var root = id / RowsPerRoot;
        var local = id % RowsPerRoot;
        if (local < 100)
            return new($"d{local:D3}", InventoryEntryKind.Directory, $"capacity:r{root:D3}:d{local:D3}", 0, Epoch, 16);
        var entry = local - 100;
        var physical = entry >= 9800 ? entry % 9800 : entry;
        var globalObject = root * 9800L + physical;
        string[] extensions = ["txt", "bin", "jpg", "log", "dat"];
        var extension = extensions[(int)((104729 + globalObject) % extensions.Length)];
        long[] boundarySizes = [0, 1, 4095, 4096, 4097, 1048576];
        var bucket = (int)((104729 + globalObject) % 17);
        var length = bucket < boundarySizes.Length ? boundarySizes[bucket] :
            (104729L + root * 73856093L + physical * 19349663L) % 65536;
        var directory = entry - entry / 100 * 100;
        var display = entry >= 9800 ? "alias-" + physical.ToString("D4", CultureInfo.InvariantCulture) : "item-" + (entry / 100).ToString("D4", CultureInfo.InvariantCulture);
        return new("d" + directory.ToString("D3", CultureInfo.InvariantCulture) + "/" + display + "." + extension,
            InventoryEntryKind.File, $"capacity:r{root:D3}:o{physical:D5}", length,
            Epoch.AddTicks((globalObject * 17 % (365L * 86400)) * TimeSpan.TicksPerSecond), 128);
    }

    private static string RootIdentity(int root) => $"capacity-r{root:D3}-{(root < 10 ? "offline" : "online")}";

    private static DatasetManifest CreateManifest(CancellationToken token, CapacityRun run, Stopwatch watch)
    {
        using var ordered = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long logicalBytes = 0, distinctBytes = 0;
        var roots = new List<RootManifest>();
        for (var root = 0; root < 100; root++)
        {
            token.ThrowIfCancellationRequested(); run.Check(watch);
            using var capture = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            AppendCaptureFrame(capture, Encoding.UTF8.GetBytes("file-organizer.capture.v1"));
            AppendCaptureFrame(capture, Encoding.UTF8.GetBytes(RootIdentity(root)));
            for (var directory = 0; directory < 100; directory++)
            {
                token.ThrowIfCancellationRequested();
                var directoryEntry = ReferenceRow(root * RowsPerRoot + directory);
                AppendMetadata(ordered, root, directoryEntry);
                AppendCaptureFrame(capture, JsonSerializer.SerializeToUtf8Bytes(directoryEntry));
                var alias = ReferenceRow(root * RowsPerRoot + 100 + 9800 + directory);
                AppendMetadata(ordered, root, alias);
                AppendCaptureFrame(capture, JsonSerializer.SerializeToUtf8Bytes(alias));
                logicalBytes += alias.Length;
                for (var group = 0; group < 98; group++)
                {
                    var row = ReferenceRow(root * RowsPerRoot + 100 + group * 100 + directory);
                    AppendMetadata(ordered, root, row);
                    AppendCaptureFrame(capture, JsonSerializer.SerializeToUtf8Bytes(row));
                    logicalBytes += row.Length;
                    distinctBytes += row.Length;
                }
            }
            roots.Add(new(root, RootIdentity(root), root < 10, 100, 9900, 9800, 100, Convert.ToHexString(capture.GetHashAndReset())));
        }
        return new(1, 104729, "arithmetic-capture-v1", 1_000_000, 990_000, 980_000, 10_000, 10_000,
            logicalBytes, distinctBytes, Convert.ToHexString(ordered.GetHashAndReset()),
            roots.ToArray());
    }

    private static void AppendCaptureFrame(IncrementalHash digest, byte[] bytes)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
        digest.AppendData(header); digest.AppendData(bytes);
    }

    private static void AppendMetadata(IncrementalHash hash, int root, InventoryEntry row)
    {
        var text = string.Join('|', RootIdentity(root), row.RelativePath, ((int)row.Kind).ToString(CultureInfo.InvariantCulture),
            row.ObjectIdentity, row.Length.ToString(CultureInfo.InvariantCulture), row.ModifiedUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            row.Attributes.ToString(CultureInfo.InvariantCulture)) + "\n";
        hash.AppendData(Encoding.UTF8.GetBytes(text));
    }

    private static int DecodeId(int root, string path)
    {
        var slash = path.IndexOf('/');
        var directory = int.Parse(path.AsSpan(1, 3), CultureInfo.InvariantCulture);
        if (slash < 0) return root * RowsPerRoot + directory;
        var name = path[(slash + 1)..];
        var entry = name.StartsWith("alias-", StringComparison.Ordinal) ? 9800 + int.Parse(name.AsSpan(6, 4), CultureInfo.InvariantCulture) :
            int.Parse(name.AsSpan(5, 4), CultureInfo.InvariantCulture) * 100 + directory;
        return root * RowsPerRoot + 100 + entry;
    }

    private static int[] ExpectedIds(QueryCase query, CancellationToken token)
    {
        var duplicateSizes = query.Family == "duplicate-size" ? DuplicateObjectCounts(query.Root, token) : null;
        var ids = new List<int>(RowsPerRoot);
        for (var local = 0; local < RowsPerRoot; local++)
        {
            if (local % 512 == 0) token.ThrowIfCancellationRequested();
            var id = query.Root * RowsPerRoot + local;
            var row = ReferenceRow(id);
            var name = row.RelativePath[(row.RelativePath.LastIndexOf('/') + 1)..];
            var selected = query.Family switch
            {
                "children" => row.RelativePath[..Math.Max(0, row.RelativePath.LastIndexOf('/'))] == query.Parent,
                "name-prefix" => name.StartsWith(query.Prefix, StringComparison.Ordinal),
                "size-order" => row.Kind == InventoryEntryKind.File && row.Length >= query.Size && row.Length <= query.Size + 4096,
                "size-filter" => row.Kind == InventoryEntryKind.File && row.Length == query.Size,
                "time-filter" => row.Kind == InventoryEntryKind.File && row.ModifiedUtc >= Epoch.AddDays(query.Day) && row.ModifiedUtc < Epoch.AddDays(query.Day + 1),
                "type-filter" => row.Kind == InventoryEntryKind.File && name.EndsWith("." + query.Extension, StringComparison.Ordinal),
                "root-filter" => true,
                "offline-filter" => (query.Root < 10) == query.Offline,
                "duplicate-size" => row.Kind == InventoryEntryKind.File && row.Length == query.Size && duplicateSizes![row.Length] > 1,
                _ => throw new InvalidOperationException("capacity.query_family"),
            };
            if (selected) ids.Add(id);
        }
        var comparisons = 0;
        ids.Sort((left, right) =>
        {
            if (++comparisons % 4096 == 0) token.ThrowIfCancellationRequested();
            var a = ReferenceRow(left); var b = ReferenceRow(right);
            var size = query.OrderBySize ? a.Length.CompareTo(b.Length) : 0;
            return size != 0 ? size : StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath);
        });
        return ids.ToArray();
    }

    private static Dictionary<long, int> DuplicateObjectCounts(int root, CancellationToken token)
    {
        var sizes = new Dictionary<long, int>();
        for (var item = 0; item < 9800; item++)
        {
            if (item % 512 == 0) token.ThrowIfCancellationRequested();
            var size = ReferenceRow(root * RowsPerRoot + 100 + item).Length;
            sizes[size] = sizes.GetValueOrDefault(size) + 1;
        }
        return sizes;
    }

    private static string QuerySql(QueryCase query)
    {
        var filter = query.Family switch
        {
            "children" => "e.ParentPath=$parent",
            "name-prefix" => "e.Name LIKE $prefix",
            "size-order" => "e.Kind=0 AND e.Length BETWEEN $size AND $sizeEnd",
            "size-filter" => "e.Kind=0 AND e.Length=$size",
            "time-filter" => "e.Kind=0 AND e.ModifiedUtc >= $from AND e.ModifiedUtc < $until",
            "type-filter" => "e.Kind=0 AND e.Name LIKE $extension",
            "root-filter" => "1=1",
            "offline-filter" => "r.RootIdentity LIKE $offline",
            "duplicate-size" => "e.Kind=0 AND e.Length=$size AND EXISTS(SELECT 1 FROM InventoryFileObject o WHERE o.GenerationId=$generation AND o.Kind=0 AND o.Length=$size GROUP BY o.Length HAVING count(*)>1)",
            _ => throw new InvalidOperationException("capacity.query_family"),
        };
        var cursor = query.OrderBySize ? "($lastPath IS NULL OR e.Length>$lastSize OR (e.Length=$lastSize AND e.RelativePath>$lastPath))" : "($lastPath IS NULL OR e.RelativePath>$lastPath)";
        var order = query.OrderBySize ? "e.Length,e.RelativePath" : "e.RelativePath";
        return $"SELECT e.RelativePath,e.Length FROM InventoryDirectoryEntry e JOIN InventoryPublishedRoot r ON r.GenerationId=e.GenerationId WHERE e.GenerationId=$generation AND r.RootIdentity=$root AND ({filter}) AND {cursor} ORDER BY {order} LIMIT 200;";
    }

    private static SqliteCommand QueryCommand(SqliteConnection connection, QueryCase query, string generation, PageCursor? cursor, bool explain = false)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = (explain ? "EXPLAIN QUERY PLAN " : string.Empty) + QuerySql(query);
        var names = Regex.Matches(command.CommandText, @"\$[A-Za-z][A-Za-z0-9]*").Select(match => match.Value).ToHashSet(StringComparer.Ordinal);
        Add("$generation", generation);
        Add("$root", RootIdentity(query.Root));
        Add("$parent", query.Parent);
        Add("$prefix", query.Prefix + "%");
        Add("$size", query.Size);
        Add("$sizeEnd", query.Size + 4096);
        Add("$from", Epoch.AddDays(query.Day).ToString("O", CultureInfo.InvariantCulture));
        Add("$until", Epoch.AddDays(query.Day + 1).ToString("O", CultureInfo.InvariantCulture));
        Add("$extension", "%." + query.Extension);
        Add("$offline", query.Offline ? "%-offline" : "%-online");
        Add("$lastPath", (object?)cursor?.Path ?? DBNull.Value);
        Add("$lastSize", cursor?.Size ?? -1L);
        return command;

        void Add(string name, object value) { if (names.Contains(name)) command.Parameters.AddWithValue(name, value); }
    }

    private static QueryPage ReadPage(SqliteConnection connection, QueryCase query, string generation, PageCursor? cursor, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var command = QueryCommand(connection, query, generation, cursor);
        using var reader = command.ExecuteReader();
        var ids = new List<int>(PageSize);
        PageCursor? next = null;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var path = reader.GetString(0);
            var size = reader.GetInt64(1);
            var id = DecodeId(query.Root, path);
            var expected = ReferenceRow(id);
            Assert.Equal(expected.RelativePath, path);
            Assert.Equal(expected.Length, size);
            ids.Add(id);
            next = new(path, size);
        }
        return new(ids.ToArray(), next);
    }

    private static string VerifyAllPages(SqliteConnection connection, QueryCase query, string generation, int[] expected,
        CancellationToken token, CapacityRun run, Stopwatch watch)
    {
        var position = 0;
        PageCursor? cursor = null;
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[4];
        while (true)
        {
            run.Check(watch);
            var page = ReadPage(connection, query, generation, cursor, token);
            foreach (var id in page.Ids)
            {
                Assert.True(position < expected.Length);
                Assert.Equal(expected[position++], id);
                BinaryPrimitives.WriteInt32LittleEndian(bytes, id);
                digest.AppendData(bytes);
            }
            if (page.Ids.Length < PageSize) break;
            Assert.NotEqual(cursor, page.Cursor);
            cursor = page.Cursor;
        }
        Assert.Equal(expected.Length, position);
        return Convert.ToHexString(digest.GetHashAndReset());
    }

    private static string[] Explain(SqliteConnection connection, QueryCase query, string generation, CancellationToken token)
    {
        using var command = QueryCommand(connection, query, generation, null, explain: true);
        using var reader = command.ExecuteReader();
        var plans = new List<string>();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); plans.Add(reader.GetString(3)); }
        return plans.ToArray();
    }

    private static void VerifyDuplicateGroup(SqliteConnection connection, QueryCase query, string generation, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Length,count(*) FROM InventoryFileObject WHERE GenerationId=$generation AND Kind=0 AND Length=$size GROUP BY Length HAVING count(*)>1;";
        command.Parameters.AddWithValue("$generation", generation);
        command.Parameters.AddWithValue("$size", query.Size);
        using var reader = command.ExecuteReader();
        token.ThrowIfCancellationRequested();
        var count = DuplicateObjectCounts(query.Root, token).GetValueOrDefault(query.Size);
        Assert.Equal(count > 1, reader.Read());
        if (count > 1) { Assert.Equal(query.Size, reader.GetInt64(0)); Assert.Equal(count, reader.GetInt32(1)); }
        Assert.False(reader.Read());
    }

    private static void VerifyDataset(SqliteConnection connection, DatasetManifest manifest, CancellationToken token, CapacityRun run, Stopwatch watch)
    {
        Assert.Equal(1_000_000L, Scalar(connection, "SELECT count(*) FROM InventoryDirectoryEntry;"));
        Assert.Equal(990_000L, Scalar(connection, "SELECT count(*) FROM InventoryFileObject;"));
        Assert.Equal(980_000L, Scalar(connection, "SELECT count(*) FROM InventoryFileObject WHERE Kind=0;"));
        Assert.Equal(10_000L, Scalar(connection, "SELECT count(*) FROM InventoryDirectoryEntry WHERE Kind=1;"));
        Assert.Equal(100L, Scalar(connection, "SELECT count(*) FROM InventoryPublishedRoot;"));
        Assert.Equal(10L, Scalar(connection, "SELECT count(*) FROM InventoryPublishedRoot WHERE RootIdentity LIKE '%-offline';"));
        Assert.Equal(manifest.LogicalBytes, Scalar(connection, "SELECT sum(Length) FROM InventoryDirectoryEntry WHERE Kind=0;"));
        Assert.Equal(manifest.DistinctObjectBytes, Scalar(connection, "SELECT sum(Length) FROM InventoryFileObject WHERE Kind=0;"));
        Assert.Equal(0L, Scalar(connection, "SELECT count(*) FROM InventoryDirectoryEntry e JOIN InventoryFileObject o ON o.GenerationId=e.GenerationId AND o.ObjectIdentity=e.ObjectIdentity WHERE e.Kind<>o.Kind OR e.Length<>o.Length OR e.ModifiedUtc<>o.ModifiedUtc OR e.Attributes<>o.Attributes;"));
        var seen = new BitArray(1_000_000);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = "SELECT r.RootIdentity,e.RelativePath,e.Kind,e.ObjectIdentity,e.Length,e.ModifiedUtc,e.Attributes FROM InventoryPublishedRoot r JOIN InventoryDirectoryEntry e ON e.GenerationId=r.GenerationId ORDER BY r.RootIdentity,e.RelativePath;";
        using var reader = command.ExecuteReader();
        var count = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (count % 1000 == 0) run.Check(watch);
            var rootId = reader.GetString(0);
            var root = int.Parse(rootId.AsSpan(10, 3), CultureInfo.InvariantCulture);
            Assert.Equal(RootIdentity(root), rootId);
            var path = reader.GetString(1);
            var id = DecodeId(root, path);
            Assert.InRange(id, 0, 999_999);
            Assert.False(seen[id]);
            seen[id] = true;
            var expected = ReferenceRow(id);
            Assert.Equal(expected.Kind, (InventoryEntryKind)reader.GetInt32(2));
            Assert.Equal(expected.ObjectIdentity, reader.GetString(3));
            Assert.Equal(expected.Length, reader.GetInt64(4));
            Assert.Equal(expected.ModifiedUtc, DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
            Assert.Equal(expected.Attributes, reader.GetInt64(6));
            Assert.Equal(expected.RelativePath, path);
            AppendMetadata(digest, root, expected);
            count++;
        }
        Assert.Equal(1_000_000, count);
        Assert.Equal(manifest.OrderedMetadataSha256, Convert.ToHexString(digest.GetHashAndReset()));
        reader.Close();
        VerifyIntegrity(connection, token);
    }

    private static void VerifyIntegrity(SqliteConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Assert.Equal("ok", Scalar(connection, "PRAGMA integrity_check;"));
        token.ThrowIfCancellationRequested();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        Assert.False(reader.Read());
        token.ThrowIfCancellationRequested();
    }

    private static void VerifyTimedAggregates(SqliteConnection connection, DatasetManifest manifest, CancellationToken token,
        CapacityRun run, Stopwatch budget)
    {
        token.ThrowIfCancellationRequested(); run.Check(budget);
        var watch = Stopwatch.StartNew();
        var entries = Scalar(connection, "SELECT count(*) FROM InventoryDirectoryEntry;");
        token.ThrowIfCancellationRequested();
        var objects = Scalar(connection, "SELECT count(*) FROM InventoryFileObject;");
        token.ThrowIfCancellationRequested();
        var logicalBytes = Scalar(connection, "SELECT sum(Length) FROM InventoryDirectoryEntry WHERE Kind=0;");
        token.ThrowIfCancellationRequested();
        var distinctBytes = Scalar(connection, "SELECT sum(Length) FROM InventoryFileObject WHERE Kind=0;");
        watch.Stop();
        token.ThrowIfCancellationRequested(); run.Check(budget);
        Assert.Equal(manifest.Entries, entries);
        Assert.Equal(manifest.Objects, objects);
        Assert.Equal(manifest.LogicalBytes, logicalBytes);
        Assert.Equal(manifest.DistinctObjectBytes, distinctBytes);
        run.WriteJson("aggregate-timings.json", new
        {
            entries, objects, logicalBytes, distinctBytes, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds,
            queries = new[] { "all entry count", "all distinct-object count", "logical file-entry byte sum", "distinct file-object byte sum" },
            proposedAggregateThresholdMilliseconds = 2000, observedWithinProposal = watch.Elapsed.TotalMilliseconds <= 2000,
            acceptance = "One measured aggregate bundle, excluding the full-row arithmetic oracle. Observation only; reference-host and owner performance acceptance remain open.",
        });
    }

    private static object ReadSchema(SqliteConnection connection, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type,name,tbl_name,coalesce(sql,'') FROM sqlite_master ORDER BY type,name;";
        using var reader = command.ExecuteReader();
        var entries = new List<string>();
        while (reader.Read()) { token.ThrowIfCancellationRequested(); entries.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3))); }
        var bytes = Encoding.UTF8.GetBytes(string.Join('\n', entries));
        return new { sha256 = Convert.ToHexString(SHA256.HashData(bytes)), entries };
    }

    private static void VerifyOnlineBackup(SqliteConnection source, CapacityRun run, CancellationToken token, Stopwatch watch)
    {
        run.Check(watch);
        token.ThrowIfCancellationRequested();
        var sourceBytes = new FileInfo(run.Paths.DatabasePath).Length;
        var logicalBackupBytes = checked(Convert.ToInt64(Scalar(source, "PRAGMA page_count;"), CultureInfo.InvariantCulture) *
            Convert.ToInt64(Scalar(source, "PRAGMA page_size;"), CultureInfo.InvariantCulture));
        if (sourceBytes > 2L << 30 || logicalBackupBytes > 2L << 30 || new DriveInfo(Path.GetPathRoot(run.Root)!).AvailableFreeSpace < (12L << 30) + (2L << 30))
            throw new InvalidOperationException("capacity.backup_reserve");
        var path = Path.Combine(run.Root, "verified-backup.db");
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (var backup = Open(path))
        {
            source.BackupDatabase(backup);
            token.ThrowIfCancellationRequested(); run.Check(watch);
            VerifyIntegrity(backup, token);
            Assert.Equal(1_000_000L, Scalar(backup, "SELECT count(*) FROM InventoryDirectoryEntry;"));
            Assert.Equal(990_000L, Scalar(backup, "SELECT count(*) FROM InventoryFileObject;"));
            Assert.Equal(980_000L, Scalar(backup, "SELECT count(*) FROM InventoryFileObject WHERE Kind=0;"));
            Assert.Equal(100L, Scalar(backup, "SELECT count(*) FROM InventoryPublishedRoot;"));
            run.WriteJson("backup-verification.json", new { outcome = "Passed", bytes = new FileInfo(path).Length,
                inventoryEntries = 1_000_000, inventoryObjects = 990_000, roots = 100, integrity = "ok", foreignKeys = "ok" });
        }
        run.Check(watch);
    }

    private static string Generation(SqliteConnection connection, int root)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT GenerationId FROM InventoryPublishedRoot WHERE RootIdentity=$root;";
        command.Parameters.AddWithValue("$root", RootIdentity(root));
        return (string?)command.ExecuteScalar() ?? throw new InvalidOperationException("capacity.missing_root");
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand(); command.CommandText = sql; command.CommandTimeout = 120;
        return command.ExecuteScalar();
    }

    private static string? ReadHeadIdentity()
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = ProcessInventorySession.FindCheckout(), RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("rev-parse"); start.ArgumentList.Add("HEAD");
        using var process = Process.Start(start);
        if (process is null) return null;
        if (!process.WaitForExit(5000)) { process.Kill(entireProcessTree: false); return null; }
        var value = process.StandardOutput.ReadToEnd().Trim();
        return process.ExitCode == 0 && value.Length == 40 && value.All(Uri.IsHexDigit) ? value : null;
    }

    private sealed record RootManifest(int Root, string Identity, bool Offline, int Directories, int FileEntries, int FileObjects, int Aliases, string CaptureFingerprint);
    private sealed record DatasetManifest(int Version, int Seed, string Algorithm, long Entries, long Objects, long FileObjects,
        long Directories, long Aliases, long LogicalBytes, long DistinctObjectBytes, string OrderedMetadataSha256, RootManifest[] Roots);
    private sealed record PageCursor(string Path, long Size);
    private sealed record QueryPage(int[] Ids, PageCursor? Cursor);
    private sealed record QueryReceipt(string Family, int Number, int Root, bool RootOffline, bool QueryOffline,
        string Prefix, long Size, int Day, string Extension, int ExpectedRows, string OrderedIdSha256,
        int PageSize, double[] FirstPageMilliseconds, string[] Plan, string Sql);

    private static QueryReceipt CreateQueryReceipt(QueryCase query, int expectedRows, string hash, double[] samples, string[] plan) =>
        new(query.Family, query.Number, query.Root, query.Root < 10, query.Offline, query.Prefix, query.Size, query.Day,
            query.Extension, expectedRows, hash, PageSize, samples, plan, QuerySql(query));

    private static byte[] SerializeQueryFamily(string family, IReadOnlyList<QueryReceipt> receipts)
    {
        if (!Families.Contains(family, StringComparer.Ordinal) || receipts.Count > 100 ||
            receipts.Any(receipt => receipt.Family != family))
            throw new InvalidOperationException("capacity.query_receipt_family");
        return JsonSerializer.SerializeToUtf8Bytes(receipts, new JsonSerializerOptions
        {
            WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }
    private sealed record QueryCase(string Family, int Number, int Root, string Parent, string Prefix, long Size, int Day, string Extension, bool Offline)
    {
        public bool OrderBySize => Family is "size-order" or "duplicate-size";
        public static QueryCase Create(string family, int number)
        {
            var root = (104729 + number * 37) % 100;
            long[] sizes = [0, 1, 4095, 4096, 4097, 1048576];
            string[] types = ["txt", "bin", "jpg", "log", "dat"];
            return new(family, number, root, number % 10 == 0 ? string.Empty : $"d{(number * 19 + 7) % 100:D3}",
                number == 0 ? "not-present" : number == 1 ? "alias-0000." : number % 10 == 0 ? "item-000" : $"item-{number % 98:D4}.",
                sizes[number % sizes.Length], number * 7 % 200, types[number % types.Length], number % 2 == 0);
        }
    }

    private sealed class BudgetProgress(CapacityRun run, Stopwatch watch, Action<InventoryCaptureProgress>? action = null) : IProgress<InventoryCaptureProgress>
    {
        public void Report(InventoryCaptureProgress value) { run.Check(watch); action?.Invoke(value); }
    }

    private sealed class CapacityRun : IDisposable
    {
        private readonly WindowsDirectoryPins pins = new();
        private readonly string? originalTemp;
        private readonly string? originalTempLong;
        private readonly string? originalSqliteTemp;
        private long receiptBytes;
        public string Root { get; }
        public AppDataPaths Paths { get; }
        public string Temp { get; }
        private CapacityRun(string root)
        {
            Root = root; Paths = new AppDataPaths(Path.Combine(root, "profile")); Temp = Path.Combine(root, "sqlite-temp");
            originalTemp = Environment.GetEnvironmentVariable("TMP"); originalTempLong = Environment.GetEnvironmentVariable("TEMP"); originalSqliteTemp = Environment.GetEnvironmentVariable("SQLITE_TMPDIR");
        }
        public static CapacityRun Create()
        {
            var checkout = ProcessInventorySession.FindCheckout();
            var root = Path.Combine(checkout, "artifacts", "FO-005-streaming-20261029", "capacity", Guid.NewGuid().ToString("N"));
            var run = new CapacityRun(root);
            try
            {
                run.pins.PinAncestors(checkout);
                var parent = checkout;
                foreach (var part in new[] { "artifacts", "FO-005-streaming-20261029", "capacity" })
                { run.pins.CreateChildDirectory(parent, part, true); parent = Path.Combine(parent, part); }
                run.pins.CreateChildDirectory(parent, Path.GetFileName(root), false);
                run.pins.CreateChildDirectory(root, "profile", false);
                run.pins.CreateChildDirectory(run.Paths.RootDirectory, "data", false);
                run.pins.CreateChildDirectory(run.Paths.RootDirectory, "logs", false);
                run.pins.CreateChildDirectory(run.Paths.DataDirectory, "backups", false);
                run.pins.CreateChildDirectory(root, "sqlite-temp", false);
                Environment.SetEnvironmentVariable("TMP", run.Temp);
                Environment.SetEnvironmentVariable("TEMP", run.Temp);
                Environment.SetEnvironmentVariable("SQLITE_TMPDIR", run.Temp);
                run.WriteJson("run-owner.json", new { format = "FileOrganizer.Capacity.v1", seed = 104729, rootId = Path.GetFileName(root), metadataOnly = true });
                return run;
            }
            catch { run.Dispose(); throw; }
        }
        public void WriteJson(string name, object value)
            => WriteReceiptBytes(name, JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true }));

        public void WriteQueryFamily(string family, IReadOnlyList<QueryReceipt> receipts)
            => WriteReceiptBytes($"queries-{family}.json", SerializeQueryFamily(family, receipts));

        private void WriteReceiptBytes(string name, byte[] bytes)
        {
            if (name.IndexOfAny(['/', '\\', ':']) >= 0 || !name.EndsWith(".json", StringComparison.Ordinal)) throw new InvalidOperationException("capacity.receipt_name");
            pins.Validate();
            if (bytes.Length > 8 * 1024 * 1024 || bytes.Length > (64L << 20) - receiptBytes) throw new InvalidOperationException("capacity.receipt_budget");
            using var handle = pins.OpenNewFile(Root, name);
            using var stream = new FileStream(handle, FileAccess.Write); stream.Write(bytes); stream.Flush(true);
            receiptBytes += bytes.Length;
        }
        public object Observe()
        {
            using var process = Process.GetCurrentProcess();
            return new { workingSetBytes = process.WorkingSet64, peakWorkingSetBytes = process.PeakWorkingSet64,
                mainDbBytes = Length(Paths.DatabasePath), walBytes = Length(Paths.DatabasePath + "-wal"), receiptBytes,
                freeBytes = new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace };
        }
        public void Check(Stopwatch watch)
        {
            pins.Validate();
            using var process = Process.GetCurrentProcess();
            if (watch.Elapsed > TimeSpan.FromMinutes(45)) throw new InvalidOperationException("capacity.duration_budget");
            if (process.WorkingSet64 > 1L << 30 || process.PeakWorkingSet64 > 1L << 30) throw new InvalidOperationException("capacity.rss_budget");
            if (Length(Paths.DatabasePath) > 2L << 30 || Length(Paths.DatabasePath + "-wal") > 512L << 20) throw new InvalidOperationException("capacity.database_budget");
            if (new DriveInfo(Path.GetPathRoot(Root)!).AvailableFreeSpace < 12L << 30) throw new InvalidOperationException("capacity.free_space_budget");
            long total = 0, temp = 0;
            var pending = new Stack<string>(); pending.Push(Root);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("capacity.reparse");
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    var attributes = File.GetAttributes(entry);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidOperationException("capacity.reparse");
                    if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(entry);
                    else
                    {
                        var length = new FileInfo(entry).Length; total = checked(total + length);
                        if (entry.StartsWith(Temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) temp += length;
                        if (entry.EndsWith("verified-backup.db", StringComparison.Ordinal) && length > 2L << 30) throw new InvalidOperationException("capacity.backup_budget");
                    }
                }
            }
            if (total > 6L << 30 || temp > 256L << 20) throw new InvalidOperationException("capacity.run_or_temp_budget");
        }
        private static long Length(string path) => File.Exists(path) ? new FileInfo(path).Length : 0;
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("TMP", originalTemp);
            Environment.SetEnvironmentVariable("TEMP", originalTempLong);
            Environment.SetEnvironmentVariable("SQLITE_TMPDIR", originalSqliteTemp);
            pins.Dispose();
        }
    }
}

public sealed class CapacityBenchmarkFactAttribute : FactAttribute
{
    public CapacityBenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FO_RUN_CAPACITY") != "1")
            Skip = "B01 million-record capacity is opt-in: FO_RUN_CAPACITY=1 and an explicit filtered Release run are required; not executed by ordinary regressions.";
        else if (typeof(InventoryCapacityBenchmark).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                     .Cast<System.Reflection.AssemblyConfigurationAttribute>().SingleOrDefault()?.Configuration != "Release")
            Skip = "The opted-in B01 capacity benchmark requires the reviewed Release build; Debug execution is not admitted.";
        else if (Debugger.IsAttached)
            Skip = "The opted-in B01 capacity benchmark requires no attached debugger; reference-host timing is not admitted under a debugger.";
    }
}
