// T-0067: retain commit provenance; migrating legacy current pairs never invents identity proof.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

internal static class InventoryComparisonSchema
{
    private const string Migration = """
        CREATE TABLE InventoryGenerationCommit (
            OperationId TEXT NOT NULL PRIMARY KEY CHECK(length(OperationId)=32),
            RootIdentity TEXT NOT NULL,
            GenerationId TEXT NOT NULL REFERENCES InventoryScan(GenerationId),
            Fingerprint TEXT NOT NULL CHECK(length(Fingerprint)=64),
            RequestHash TEXT NOT NULL CHECK(length(RequestHash)=64),
            JournalIdentity TEXT NOT NULL,
            NextUsn INTEGER NOT NULL CHECK(NextUsn>=0),
            NoticeSequence INTEGER NOT NULL CHECK(NoticeSequence>=0),
            Issues INTEGER NOT NULL CHECK(Issues>=0 AND Issues<=31),
            Coverage INTEGER NOT NULL CHECK(Coverage BETWEEN 0 AND 2),
            IdentityEvidence INTEGER NOT NULL CHECK(IdentityEvidence BETWEEN 0 AND 1),
            SourceSchema INTEGER NOT NULL CHECK(SourceSchema IN (4,5)),
            ComparisonFormat INTEGER NOT NULL CHECK(ComparisonFormat=1)
        );
        CREATE INDEX InventoryGenerationCommitLookup ON InventoryGenerationCommit(RootIdentity,GenerationId);
        INSERT INTO InventoryGenerationCommit
            SELECT OperationId,RootIdentity,GenerationId,Fingerprint,RequestHash,JournalIdentity,NextUsn,
                NoticeSequence,Issues,1,0,4,1 FROM InventoryReconciliation;
        CREATE TRIGGER InventoryGenerationCommitNoUpdate BEFORE UPDATE ON InventoryGenerationCommit
            BEGIN SELECT RAISE(ABORT,'immutable generation commit'); END;
        CREATE TRIGGER InventoryGenerationCommitNoDelete BEFORE DELETE ON InventoryGenerationCommit
            BEGIN SELECT RAISE(ABORT,'immutable generation commit'); END;
        """;
    internal static string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Migration)));

    internal static void Ensure(SqliteConnection connection, string dataDirectory, CancellationToken token, Action? beforeCommit = null)
    {
        InventoryReconciliationSchema.Ensure(connection, dataDirectory, token, beforeCommit);
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= 5) return;
        if (version != 4) throw new StoreInitializationException("store.invalid_schema");
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(directory, $"before-inventory-v5-{Guid.NewGuid():N}.db");
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            backup.Open(); connection.BackupDatabase(backup);
            if (Convert.ToInt32(InventorySchema.Scalar(backup, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture) != 4 ||
                Convert.ToString(InventorySchema.Scalar(backup, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture) != "ok")
                throw new StoreInitializationException("store.backup_invalid");
            SqliteApplicationStore.ValidateMigration(backup, null, 4);
        }
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        InventorySchema.Execute(connection, transaction, Migration);
        using var record = InventorySchema.Command(connection, transaction, "INSERT INTO SchemaMigration VALUES(5,$checksum,$utc);");
        record.Parameters.AddWithValue("$checksum", Checksum);
        record.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        record.ExecuteNonQuery();
        InventorySchema.Execute(connection, transaction, "PRAGMA user_version=5;");
        SqliteApplicationStore.ValidateMigration(connection, transaction, 5);
        beforeCommit?.Invoke();
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
