using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

internal static class InventorySchema
{
    private const string Migration = """
        CREATE TABLE InventoryScan (
            GenerationId TEXT NOT NULL PRIMARY KEY,
            RootIdentity TEXT NOT NULL,
            Fingerprint TEXT NOT NULL,
            Status TEXT NOT NULL CHECK(Status IN ('Running','Interrupted','Cancelled','Incomplete','Completed')),
            CommittedEntries INTEGER NOT NULL CHECK(CommittedEntries>=0),
            TotalEntries INTEGER NOT NULL CHECK(TotalEntries>=CommittedEntries),
            UNIQUE(RootIdentity,Fingerprint)
        );
        CREATE TABLE InventoryFileObject (
            GenerationId TEXT NOT NULL REFERENCES InventoryScan(GenerationId),
            ObjectIdentity TEXT NOT NULL,
            Kind INTEGER NOT NULL,
            Length INTEGER NOT NULL CHECK(Length>=0),
            ModifiedUtc TEXT NOT NULL,
            Attributes INTEGER NOT NULL,
            PRIMARY KEY(GenerationId,ObjectIdentity)
        );
        CREATE TABLE InventoryDirectoryEntry (
            GenerationId TEXT NOT NULL REFERENCES InventoryScan(GenerationId),
            Ordinal INTEGER NOT NULL CHECK(Ordinal>=0),
            RelativePath TEXT NOT NULL,
            ParentPath TEXT NOT NULL,
            Name TEXT NOT NULL,
            Kind INTEGER NOT NULL,
            ObjectIdentity TEXT NULL,
            Length INTEGER NOT NULL CHECK(Length>=0),
            ModifiedUtc TEXT NOT NULL,
            Attributes INTEGER NOT NULL,
            ErrorCode TEXT NULL,
            PRIMARY KEY(GenerationId,Ordinal),
            UNIQUE(GenerationId,RelativePath),
            FOREIGN KEY(GenerationId,ObjectIdentity) REFERENCES InventoryFileObject(GenerationId,ObjectIdentity)
        );
        CREATE INDEX InventoryChildren ON InventoryDirectoryEntry(GenerationId,ParentPath,Name,Ordinal);
        CREATE TABLE InventoryPublishedRoot (
            RootIdentity TEXT NOT NULL PRIMARY KEY,
            GenerationId TEXT NOT NULL REFERENCES InventoryScan(GenerationId)
        );
        """;

    internal static string Checksum { get; } = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Migration)));

    internal static void Ensure(SqliteConnection connection, string dataDirectory, CancellationToken token)
    {
        var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > 6) throw new StoreInitializationException("store.newer_schema");
        if (version < 1) throw new StoreInitializationException("store.invalid_schema");
        if (Convert.ToString(Scalar(connection, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture) != "ok")
            throw new StoreInitializationException("store.corrupt");
        SqliteApplicationStore.ValidateMigration(connection, null, version);
        if (version >= 2) return;
        token.ThrowIfCancellationRequested();

        var backups = Path.Combine(dataDirectory, "backups");
        Directory.CreateDirectory(backups);
        var backupPath = Path.Combine(backups, $"before-inventory-v2-{Guid.NewGuid():N}.db");
        using (new FileStream(backupPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        using (var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString()))
        {
            backup.Open();
            connection.BackupDatabase(backup);
            if (Convert.ToInt32(Scalar(backup, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture) != 1 ||
                Convert.ToString(Scalar(backup, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture) != "ok")
                throw new StoreInitializationException("store.backup_invalid");
            SqliteApplicationStore.ValidateMigration(backup, null, 1);
        }

        token.ThrowIfCancellationRequested();
        using var transaction = connection.BeginTransaction();
        Execute(connection, transaction, Migration);
        using (var command = Command(connection, transaction,
            "INSERT INTO SchemaMigration(Version,Checksum,AppliedUtc) VALUES(2,$checksum,$utc);"))
        {
            command.Parameters.AddWithValue("$checksum", Checksum);
            command.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            command.ExecuteNonQuery();
        }
        Execute(connection, transaction, "PRAGMA user_version=2;");
        SqliteApplicationStore.ValidateMigration(connection, transaction, 2);
        token.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    internal static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = 5;
        return command;
    }

    internal static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteNonQuery();
    }

    internal static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar();
    }
}
