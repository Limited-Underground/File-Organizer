using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

internal static class OperationJournalSchema
{
    private const string Migration = """
        CREATE TABLE OperationJournal (
            OperationId TEXT NOT NULL CHECK(length(OperationId)=32),
            Sequence INTEGER NOT NULL CHECK(Sequence>=0 AND Sequence<256),
            Format INTEGER NOT NULL CHECK(Format=1),
            Payload BLOB NOT NULL CHECK(typeof(Payload)='blob' AND length(Payload) BETWEEN 1 AND 65536),
            PRIMARY KEY(OperationId,Sequence)
        );
        CREATE TRIGGER OperationJournalNoUpdate BEFORE UPDATE ON OperationJournal
            BEGIN SELECT RAISE(ABORT,'immutable operation journal'); END;
        CREATE TRIGGER OperationJournalNoDelete BEFORE DELETE ON OperationJournal
            BEGIN SELECT RAISE(ABORT,'immutable operation journal'); END;
        """;

    internal static string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Migration)));

    internal static void Ensure(SqliteConnection connection, string dataDirectory, CancellationToken token)
    {
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version is < 1 or > 6) throw new StoreInitializationException("store.invalid_schema");
        SqliteApplicationStore.ValidateMigration(connection, null, version);
        if (version == 6) return;
        InventoryComparisonSchema.Ensure(connection, dataDirectory, token);
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(directory, $"before-operation-v6-{Guid.NewGuid():N}.db");
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            backup.Open();
            connection.BackupDatabase(backup);
            if (Convert.ToInt32(InventorySchema.Scalar(backup, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture) != 5 ||
                Convert.ToString(InventorySchema.Scalar(backup, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture) != "ok")
                throw new StoreInitializationException("store.backup_invalid");
            SqliteApplicationStore.ValidateMigration(backup, null, 5);
        }
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        InventorySchema.Execute(connection, transaction, Migration);
        using var record = InventorySchema.Command(connection, transaction,
            "INSERT INTO SchemaMigration VALUES(6,$checksum,$utc);");
        record.Parameters.AddWithValue("$checksum", Checksum);
        record.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        record.ExecuteNonQuery();
        InventorySchema.Execute(connection, transaction, "PRAGMA user_version=6;");
        SqliteApplicationStore.ValidateMigration(connection, transaction, 6);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
