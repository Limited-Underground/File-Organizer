using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

internal static class InventoryCaptureSchema
{
    private const string Migration = """
        CREATE TABLE InventoryCapture (
            GenerationId TEXT NOT NULL PRIMARY KEY REFERENCES InventoryScan(GenerationId),
            Phase TEXT NOT NULL CHECK(Phase IN ('Capturing','Verifying','Completed','Incomplete')),
            OptionsHash TEXT NOT NULL,
            DeadlineUtc TEXT NOT NULL,
            CanonicalFingerprint TEXT NULL,
            CoverageErrors INTEGER NOT NULL DEFAULT 0 CHECK(CoverageErrors>=0),
            CapturedDirectories INTEGER NOT NULL DEFAULT 0 CHECK(CapturedDirectories>=0),
            VerifiedEntries INTEGER NOT NULL DEFAULT 0 CHECK(VerifiedEntries>=0),
            VerificationEpoch TEXT NULL
        );
        CREATE TABLE InventoryCaptureDirectory (
            GenerationId TEXT NOT NULL REFERENCES InventoryCapture(GenerationId),
            RelativePath TEXT NOT NULL,
            ExpectedIdentity TEXT NOT NULL,
            State TEXT NOT NULL CHECK(State IN ('Pending','Enumerating','Complete','Error')),
            CaptureEpoch TEXT NULL,
            ErrorCode TEXT NULL,
            VerificationState TEXT NOT NULL DEFAULT 'Pending' CHECK(VerificationState IN ('Pending','Complete','Error')),
            PRIMARY KEY(GenerationId,RelativePath),
            UNIQUE(GenerationId,ExpectedIdentity)
        );
        CREATE INDEX InventoryCaptureFrontier ON InventoryCaptureDirectory(GenerationId,State,RelativePath);
        CREATE INDEX InventoryVerifyFrontier ON InventoryCaptureDirectory(GenerationId,VerificationState,RelativePath);
        CREATE INDEX InventoryEntryObjects ON InventoryDirectoryEntry(GenerationId,ObjectIdentity);
        CREATE TABLE InventoryCaptureObserved (
            GenerationId TEXT NOT NULL,
            RelativePath TEXT NOT NULL,
            PathKey TEXT NOT NULL,
            CaptureEpoch TEXT NOT NULL,
            VerificationEpoch TEXT NULL,
            PRIMARY KEY(GenerationId,RelativePath),
            UNIQUE(GenerationId,PathKey),
            FOREIGN KEY(GenerationId,RelativePath) REFERENCES InventoryDirectoryEntry(GenerationId,RelativePath)
        );
        """;

    internal static string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Migration)));

    internal static void Ensure(SqliteConnection connection, string dataDirectory, CancellationToken token)
    {
        InventorySchema.Ensure(connection, dataDirectory, token);
        var version = Convert.ToInt32(InventorySchema.Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version >= 3) return;
        if (version != 2) throw new StoreInitializationException("store.invalid_schema");
        token.ThrowIfCancellationRequested();
        var directory = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(directory);
        var backupPath = Path.Combine(directory, $"before-inventory-v3-{Guid.NewGuid():N}.db");
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            backup.Open();
            connection.BackupDatabase(backup);
            if (Convert.ToInt32(InventorySchema.Scalar(backup, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture) != 2 ||
                Convert.ToString(InventorySchema.Scalar(backup, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture) != "ok")
                throw new StoreInitializationException("store.backup_invalid");
            SqliteApplicationStore.ValidateMigration(backup, null, 2);
        }
        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        InventorySchema.Execute(connection, transaction, Migration);
        using var record = InventorySchema.Command(connection, transaction,
            "INSERT INTO SchemaMigration VALUES(3,$checksum,$utc);");
        record.Parameters.AddWithValue("$checksum", Checksum);
        record.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        record.ExecuteNonQuery();
        InventorySchema.Execute(connection, transaction, "PRAGMA user_version=3;");
        SqliteApplicationStore.ValidateMigration(connection, transaction, 3);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }
}
