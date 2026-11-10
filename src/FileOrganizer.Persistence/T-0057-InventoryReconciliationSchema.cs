// T-0057: additive v4 only. Earlier SQL/checksums remain immutable.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

internal static class InventoryReconciliationSchema
{
    private const string Migration = """
        CREATE UNIQUE INDEX InventoryPublishedGeneration ON InventoryPublishedRoot(RootIdentity,GenerationId);
        CREATE TABLE InventoryReconciliation (
            RootIdentity TEXT NOT NULL PRIMARY KEY,
            GenerationId TEXT NOT NULL REFERENCES InventoryScan(GenerationId),
            Fingerprint TEXT NOT NULL CHECK(length(Fingerprint)=64),
            OperationId TEXT NOT NULL UNIQUE CHECK(length(OperationId)=32),
            RequestHash TEXT NOT NULL CHECK(length(RequestHash)=64),
            JournalIdentity TEXT NOT NULL,
            NextUsn INTEGER NOT NULL CHECK(NextUsn>=0),
            NoticeSequence INTEGER NOT NULL CHECK(NoticeSequence>=0),
            Issues INTEGER NOT NULL CHECK(Issues>=0 AND Issues<=31),
            FOREIGN KEY(RootIdentity,GenerationId) REFERENCES InventoryPublishedRoot(RootIdentity,GenerationId)
                DEFERRABLE INITIALLY DEFERRED
        );
        """;
    internal static string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Migration)));

    internal static void Ensure(SqliteConnection connection, string dataDirectory, CancellationToken token, Action? beforeCommit = null)
    {
        InventoryCaptureSchema.Ensure(connection, dataDirectory, token);
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version == 4) return;
        if (version != 3) throw new StoreInitializationException("store.invalid_schema");
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(directory, $"before-inventory-v4-{Guid.NewGuid():N}.db");
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            backup.Open(); connection.BackupDatabase(backup);
            if (Convert.ToInt32(InventorySchema.Scalar(backup, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture) != 3 ||
                Convert.ToString(InventorySchema.Scalar(backup, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture) != "ok")
                throw new StoreInitializationException("store.backup_invalid");
            SqliteApplicationStore.ValidateMigration(backup, null, 3);
        }
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        InventorySchema.Execute(connection, transaction, Migration);
        using var record = InventorySchema.Command(connection, transaction, "INSERT INTO SchemaMigration VALUES(4,$checksum,$utc);");
        record.Parameters.AddWithValue("$checksum", Checksum);
        record.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        record.ExecuteNonQuery();
        InventorySchema.Execute(connection, transaction, "PRAGMA user_version=4;");
        SqliteApplicationStore.ValidateMigration(connection, transaction, 4);
        beforeCommit?.Invoke();
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
