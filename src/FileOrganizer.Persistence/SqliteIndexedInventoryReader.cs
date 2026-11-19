using System.Globalization;
using System.Runtime.CompilerServices;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

[assembly: InternalsVisibleTo("FileOrganizer.Tests")]

namespace FileOrganizer.Persistence;

/// <summary>
/// Reads existing catalogue state through short read-only transactions. It never creates a
/// profile/database, migrates a schema, updates startup counters or accesses indexed source paths.
/// Cancellation is cooperative; result admission and UI request ownership remain bounded.
/// </summary>
public sealed class SqliteIndexedInventoryReader : IIndexedInventoryReader
{
    private readonly AppDataPaths paths;
    private readonly Action<int>? onFilteredCandidate;

    public SqliteIndexedInventoryReader(AppDataPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        this.paths = paths;
    }

    internal SqliteIndexedInventoryReader(AppDataPaths paths, Action<int> onFilteredCandidate) : this(paths)
    {
        this.onFilteredCandidate = onFilteredCandidate;
    }

    public Task<IndexedCatalogPage> ReadCatalogsAsync(IndexedCatalogRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PageSize is < 1 or > 100 || (request.AfterRootIdentity is not null && !ValidIdentity(request.AfterRootIdentity)))
            throw new InventoryIndexException("inventory.query_invalid_request");
        return Task.Run(() => Read(() => ReadCatalogs(request, cancellationToken), cancellationToken), cancellationToken);
    }

    public Task<IndexedInventoryPage> ReadPageAsync(IndexedInventoryRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!ValidIdentity(request.RootIdentity) || !ValidGeneration(request.GenerationId) || request.PageSize is < 1 or > 200)
            throw new InventoryIndexException("inventory.query_invalid_request");
        var filter = IndexedInventoryFilter.Normalize(request.Filter);
        if (request.After is { } after && (after.RootIdentity != request.RootIdentity || after.GenerationId != request.GenerationId ||
            after.PageSize != request.PageSize || string.IsNullOrEmpty(after.AfterRelativePath) || after.AfterRelativePath.Length > 3200 ||
            after.AfterRelativePath.Any(char.IsControl)))
            throw new InventoryIndexException("inventory.query_cursor_mismatch");
        if (request.After is { } cursor)
        {
            IndexedInventoryFilter cursorFilter;
            try { cursorFilter = IndexedInventoryFilter.Normalize(cursor.Filter); }
            catch (InventoryIndexException) { throw new InventoryIndexException("inventory.query_cursor_mismatch"); }
            if (cursorFilter != filter) throw new InventoryIndexException("inventory.query_cursor_mismatch");
        }
        request = request with { Filter = filter };
        return Task.Run(() => Read(() => ReadPage(request, cancellationToken), cancellationToken), cancellationToken);
    }

    private static T Read<T>(Func<T> action, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try { return action(); }
        catch (SqliteException exception)
        {
            token.ThrowIfCancellationRequested();
            throw new InventoryIndexException(exception.SqliteErrorCode is 11 or 26 ? "inventory.query_corrupt" : "inventory.query_unavailable");
        }
        catch (IOException) { throw new InventoryIndexException("inventory.query_unavailable"); }
        catch (UnauthorizedAccessException) { throw new InventoryIndexException("inventory.query_unavailable"); }
        catch (StoreInitializationException) { throw new InventoryIndexException("inventory.query_schema_invalid"); }
        catch (FormatException) { throw new InventoryIndexException("inventory.query_invalid_metadata"); }
        catch (InvalidCastException) { throw new InventoryIndexException("inventory.query_invalid_metadata"); }
        catch (OverflowException) { throw new InventoryIndexException("inventory.query_invalid_metadata"); }
    }

    private IndexedCatalogPage ReadCatalogs(IndexedCatalogRequest request, CancellationToken token)
    {
        if (!File.Exists(paths.DatabasePath)) return new([], null);
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (ReadSchema(connection, transaction) == 1) return new([], null);
        using var command = InventorySchema.Command(connection, transaction, """
            SELECT roots.RootIdentity,p.GenerationId,s.RootIdentity,s.Status,s.CommittedEntries,s.TotalEntries,
                EXISTS(SELECT 1 FROM InventoryScan a WHERE a.RootIdentity=roots.RootIdentity AND a.Status<>'Completed')
            FROM (SELECT DISTINCT RootIdentity FROM InventoryScan WHERE RootIdentity>$after COLLATE BINARY
                  ORDER BY RootIdentity COLLATE BINARY LIMIT $take) roots
            LEFT JOIN InventoryPublishedRoot p ON p.RootIdentity=roots.RootIdentity
            LEFT JOIN InventoryScan s ON s.GenerationId=p.GenerationId
            ORDER BY roots.RootIdentity COLLATE BINARY;
            """);
        command.Parameters.AddWithValue("$after", request.AfterRootIdentity ?? string.Empty);
        command.Parameters.AddWithValue("$take", request.PageSize + 1);
        using var reader = command.ExecuteReader();
        var catalogs = new List<IndexedCatalog>(request.PageSize + 1);
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            var root = reader.GetString(0);
            if (!ValidIdentity(root)) throw new InventoryIndexException("inventory.query_invalid_metadata");
            string? generation = null;
            var count = 0;
            if (!reader.IsDBNull(1))
            {
                generation = reader.GetString(1);
                if (!ValidGeneration(generation) || reader.IsDBNull(2) || reader.GetString(2) != root || reader.GetString(3) != "Completed" ||
                    reader.GetInt32(4) != reader.GetInt32(5) || reader.GetInt32(5) is < 0 or > 1_100_000)
                    throw new InventoryIndexException("inventory.query_invalid_metadata");
                count = reader.GetInt32(5);
            }
            catalogs.Add(new(root, generation, count, reader.GetInt32(6) != 0));
        }
        string? next = null;
        if (catalogs.Count > request.PageSize)
        {
            catalogs.RemoveAt(catalogs.Count - 1);
            next = catalogs[^1].RootIdentity;
        }
        return new(catalogs.AsReadOnly(), next);
    }

    private IndexedInventoryPage ReadPage(IndexedInventoryRequest request, CancellationToken token)
    {
        if (!File.Exists(paths.DatabasePath)) throw new InventoryIndexException("inventory.query_generation_missing");
        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        if (ReadSchema(connection, transaction) == 1) throw new InventoryIndexException("inventory.query_generation_missing");
        IndexedCatalog catalog;
        string? currentGeneration;
        using (var command = InventorySchema.Command(connection, transaction, """
            SELECT s.RootIdentity,s.Status,s.CommittedEntries,s.TotalEntries,p.GenerationId,
                EXISTS(SELECT 1 FROM InventoryScan a WHERE a.RootIdentity=s.RootIdentity AND a.Status<>'Completed'),
                activeScan.RootIdentity,activeScan.Status,activeScan.CommittedEntries,activeScan.TotalEntries
            FROM InventoryScan s LEFT JOIN InventoryPublishedRoot p ON p.RootIdentity=s.RootIdentity
            LEFT JOIN InventoryScan activeScan ON activeScan.GenerationId=p.GenerationId
            WHERE s.GenerationId=$generation;
            """))
        {
            command.Parameters.AddWithValue("$generation", request.GenerationId);
            using var reader = command.ExecuteReader();
            if (!reader.Read() || reader.GetString(0) != request.RootIdentity)
                throw new InventoryIndexException("inventory.query_generation_missing");
            if (reader.GetString(1) != "Completed") throw new InventoryIndexException("inventory.query_generation_incomplete");
            if (reader.GetInt32(2) != reader.GetInt32(3) || reader.GetInt32(3) is < 0 or > 1_100_000)
                throw new InventoryIndexException("inventory.query_invalid_metadata");
            currentGeneration = reader.IsDBNull(4) ? null : reader.GetString(4);
            if (currentGeneration is not null && (!ValidGeneration(currentGeneration) || reader.IsDBNull(6) ||
                reader.GetString(6) != request.RootIdentity || reader.GetString(7) != "Completed" ||
                reader.GetInt32(8) != reader.GetInt32(9) || reader.GetInt32(9) is < 0 or > 1_100_000))
                throw new InventoryIndexException("inventory.query_invalid_metadata");
            catalog = new(request.RootIdentity, request.GenerationId, reader.GetInt32(3), reader.GetInt32(5) != 0);
        }
        var filter = IndexedInventoryFilter.Normalize(request.Filter);
        IndexedFilterEvaluation? evaluation = null;
        var filterClause = string.Empty;
        if (filter.IsActive)
        {
            evaluation = new IndexedFilterEvaluation(filter, token, onFilteredCandidate);
            connection.CreateFunction<object?, object?, object?, object?, object?, bool>("fo_indexed_match", evaluation.Match, isDeterministic: false);
            filterClause = " AND fo_indexed_match(RelativePath,Kind,Length,ModifiedUtc,ErrorCode)";
        }
        using var rows = InventorySchema.Command(connection, transaction, $$"""
            SELECT RelativePath,Kind,Length,ModifiedUtc,ErrorCode
            FROM InventoryDirectoryEntry WHERE GenerationId=$generation AND RelativePath>$after COLLATE BINARY
            {{filterClause}}
            ORDER BY RelativePath COLLATE BINARY LIMIT $take;
            """);
        rows.Parameters.AddWithValue("$generation", request.GenerationId);
        rows.Parameters.AddWithValue("$after", request.After?.AfterRelativePath ?? string.Empty);
        rows.Parameters.AddWithValue("$take", request.PageSize + 1);
        var entries = new List<IndexedInventoryRow>(request.PageSize + 1);
        try
        {
            using var result = rows.ExecuteReader();
            while (result.Read())
            {
                token.ThrowIfCancellationRequested();
                var entry = IndexedInventoryMetadata.Read(result.GetValue(0), result.GetValue(1), result.GetValue(2),
                    result.GetValue(3), result.GetValue(4));
                entries.Add(new(entry.Path, entry.Kind, entry.Length, entry.ModifiedUtc, entry.ErrorCode));
            }
        }
        catch (SqliteException)
        {
            // SQLite wraps scalar-function exceptions. Preserve caller cancellation first,
            // then the first safe evaluation-limit reason; never return partial/false-empty rows.
            token.ThrowIfCancellationRequested();
            if (evaluation?.FailureCode is { } code) throw new InventoryIndexException(code);
            throw;
        }
        IndexedInventoryCursor? next = null;
        if (entries.Count > request.PageSize)
        {
            entries.RemoveAt(entries.Count - 1);
            var cursorFilter = filter == new IndexedInventoryFilter() ? null : filter;
            next = new(request.RootIdentity, request.GenerationId, request.PageSize, entries[^1].RelativePath, cursorFilter);
        }
        token.ThrowIfCancellationRequested();
        return new(catalog, entries.AsReadOnly(), next, currentGeneration == request.GenerationId, currentGeneration);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            InventorySchema.Execute(connection, null, "PRAGMA query_only=ON; PRAGMA busy_timeout=5000;");
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static int ReadSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version is < 1 or > 6) throw new InventoryIndexException("inventory.query_schema_unsupported");
        SqliteApplicationStore.ValidateMigration(connection, transaction, version);
        return version;
    }

    private static bool ValidGeneration(string? value) => value is not null && Guid.TryParseExact(value, "N", out var id) && id.ToString("N") == value;
    private static bool ValidIdentity(string? value) => !string.IsNullOrEmpty(value) && value.Length <= 256 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is ':' or '-' or '_' or '.');
}
