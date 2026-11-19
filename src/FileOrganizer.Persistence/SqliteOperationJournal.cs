using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

/// <summary>Durable, append-only operation records. The caller holds the profile lease throughout use.</summary>
public sealed class SqliteOperationJournal(AppDataPaths paths, Action<OperationJournalEntry>? beforeCommit = null) : IOperationJournal
{
    private static readonly SemaphoreSlim Writer = new(1, 1);
    private const int MaximumRecords = 256;
    private const int MaximumRecordBytes = 65_536;
    private const int MaximumOperationBytes = 1_048_576;
    private static readonly JsonSerializerOptions Format = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        RespectRequiredConstructorParameters = true
    };

    public Task<IReadOnlyList<OperationJournalEntry>> ReadAsync(Guid id, CancellationToken token)
    {
        ValidateId(id);
        return Task.Run<IReadOnlyList<OperationJournalEntry>>(() =>
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(paths.DatabasePath)) return Array.Empty<OperationJournalEntry>();
            using var connection = Open(readOnly: true);
            using var transaction = connection.BeginTransaction(deferred: true);
            var version = Version(connection, transaction);
            ValidateSchema(connection, transaction, version);
            if (version < 6) return Array.Empty<OperationJournalEntry>();
            return ReadRecords(connection, transaction, id, token).Entries;
        }, token);
    }

    public Task AppendAsync(Guid id, OperationJournalEntry entry, CancellationToken token)
    {
        ValidateId(id);
        ArgumentNullException.ThrowIfNull(entry);
        ValidateEntry(entry);
        var payload = JsonSerializer.SerializeToUtf8Bytes(entry, Format);
        if (payload.Length is < 1 or > MaximumRecordBytes)
            throw new InvalidOperationException("Operation journal record limit exceeded.");
        return AppendSerializedAsync(id, entry, payload, token);
    }

    private async Task AppendSerializedAsync(Guid id, OperationJournalEntry entry, byte[] payload, CancellationToken token)
    {
        await Writer.WaitAsync(token).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                Append(id, entry, payload, token);
            }, token).ConfigureAwait(false);
        }
        finally { Writer.Release(); }
    }

    private void Append(Guid id, OperationJournalEntry entry, byte[] payload, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var connection = Open(readOnly: false);
        var version = Version(connection, null);
        ValidateSchema(connection, null, version);
        OperationJournalSchema.Ensure(connection, paths.DataDirectory, token);
        using var transaction = connection.BeginTransaction(deferred: false);
        var history = ReadRecords(connection, transaction, id, token);
        if (entry.Sequence != history.Entries.Count)
            throw new InvalidOperationException("Operation journal sequence mismatch.");
        if (history.Entries.Count >= MaximumRecords || payload.Length > MaximumOperationBytes - history.Bytes)
            throw new InvalidOperationException("Operation journal limit exceeded.");
        using var append = InventorySchema.Command(connection, transaction,
            "INSERT INTO OperationJournal(OperationId,Sequence,Format,Payload) VALUES($id,$sequence,1,$payload);");
        append.Parameters.AddWithValue("$id", id.ToString("N"));
        append.Parameters.AddWithValue("$sequence", entry.Sequence);
        append.Parameters.Add("$payload", SqliteType.Blob).Value = payload;
        append.ExecuteNonQuery();
        beforeCommit?.Invoke(entry);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    private static (IReadOnlyList<OperationJournalEntry> Entries, int Bytes) ReadRecords(
        SqliteConnection connection, SqliteTransaction transaction, Guid id, CancellationToken token)
    {
        using var command = InventorySchema.Command(connection, transaction, """
            SELECT Sequence,Format,Payload,length(Payload),typeof(Payload)
            FROM OperationJournal WHERE OperationId=$id ORDER BY Sequence LIMIT 257;
            """);
        command.Parameters.AddWithValue("$id", id.ToString("N"));
        using var reader = command.ExecuteReader();
        var entries = new List<OperationJournalEntry>();
        var bytes = 0;
        while (reader.Read())
        {
            token.ThrowIfCancellationRequested();
            if (entries.Count >= MaximumRecords || reader.GetValue(0) is not long sequence || sequence != entries.Count ||
                reader.GetValue(1) is not long format || format != 1 ||
                reader.GetValue(3) is not long length || length is < 1 or > MaximumRecordBytes ||
                reader.GetString(4) != "blob" || length > MaximumOperationBytes - bytes)
                throw new InvalidOperationException("Malformed operation journal.");
            var payload = (byte[])reader.GetValue(2);
            if (payload.Length != length) throw new InvalidOperationException("Malformed operation journal.");
            OperationJournalEntry entry;
            try { entry = JsonSerializer.Deserialize<OperationJournalEntry>(payload, Format)
                    ?? throw new InvalidOperationException("Malformed operation journal."); }
            catch (JsonException) { throw new InvalidOperationException("Malformed operation journal."); }
            ValidateEntry(entry);
            if (entry.Sequence != sequence) throw new InvalidOperationException("Malformed operation journal sequence.");
            entries.Add(entry);
            bytes += payload.Length;
        }
        token.ThrowIfCancellationRequested();
        return (entries.AsReadOnly(), bytes);
    }

    private SqliteConnection Open(bool readOnly)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            connection.Open();
            InventorySchema.Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
            if (readOnly) InventorySchema.Execute(connection, null, "PRAGMA query_only=ON;");
            else
            {
                if (!string.Equals(Convert.ToString(InventorySchema.Scalar(connection, null, "PRAGMA journal_mode;"),
                        CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
                    throw new StoreInitializationException("store.invalid_schema");
                InventorySchema.Execute(connection, null, "PRAGMA synchronous=FULL;");
            }
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }

    private static int Version(SqliteConnection connection, SqliteTransaction? transaction) =>
        Convert.ToInt32(InventorySchema.Scalar(connection, transaction, "PRAGMA user_version;"), CultureInfo.InvariantCulture);

    private static void ValidateSchema(SqliteConnection connection, SqliteTransaction? transaction, int version)
    {
        if (version > 6) throw new StoreInitializationException("store.newer_schema");
        if (version < 1) throw new StoreInitializationException("store.invalid_schema");
        SqliteApplicationStore.ValidateMigration(connection, transaction, version);
    }

    private static void ValidateId(Guid id)
    {
        if (id == Guid.Empty) throw new ArgumentException("An operation identifier is required.", nameof(id));
    }

    private static void ValidateEntry(OperationJournalEntry entry)
    {
        if (entry.Sequence is < 0 or >= MaximumRecords || !Enum.IsDefined(entry.Kind) ||
            (entry.Boundary is { } boundary && !Enum.IsDefined(boundary)))
            throw new InvalidOperationException("Malformed operation journal record.");
        if (entry.Kind == OperationRecordKind.Plan)
        {
            if (entry.Sequence != 0 || entry.Plan is null || entry.Boundary is not null || entry.Observation is not null)
                throw new InvalidOperationException("Malformed operation plan record.");
        }
        else if (entry.Sequence == 0 || entry.Plan is not null || entry.Boundary is null || entry.Observation is null)
            throw new InvalidOperationException("Malformed operation boundary record.");
        if (entry.Plan is { } plan)
        {
            if (!Enum.IsDefined(plan.Mode) || !ValidText(plan.SourceKey) || !ValidText(plan.DestinationKey))
                throw new InvalidOperationException("Malformed operation plan.");
            ValidateFile(plan.ExpectedSource);
        }
        if (entry.Observation is { } observation)
        {
            if (observation.Source is { } source) ValidateFile(source);
            if (observation.Partial is { } partial) ValidateFile(partial);
            if (observation.Destination is { } destination) ValidateFile(destination);
            if (observation.RetainedSource is { } retained) ValidateFile(retained);
        }
    }

    private static void ValidateFile(OperationFile? file)
    {
        if (file is null || !ValidText(file.Identity) || file.Length < 0 ||
            file.Sha256 is null || file.Sha256.Length != 64 || !file.Sha256.All(char.IsAsciiHexDigitUpper))
            throw new InvalidOperationException("Malformed operation file metadata.");
    }

    private static bool ValidText(string? value) =>
        value is { Length: > 0 and <= 1024 } && !string.IsNullOrWhiteSpace(value) && !value.Any(char.IsControl);
}