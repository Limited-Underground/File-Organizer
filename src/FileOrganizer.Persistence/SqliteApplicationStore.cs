using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using FileOrganizer.Application;
using Microsoft.Data.Sqlite;

namespace FileOrganizer.Persistence;

/// <summary>
/// Initializes only application startup state. The host must hold its exclusive
/// profile lease for this call and all subsequent store use.
/// </summary>
public sealed class SqliteApplicationStore(AppDataPaths paths) : IApplicationStore
{
    private const int CurrentSchemaVersion = 1;
    private const string InitialMigration = """
        CREATE TABLE SchemaMigration (
            Version INTEGER NOT NULL PRIMARY KEY CHECK (Version > 0),
            Checksum TEXT NOT NULL,
            AppliedUtc TEXT NOT NULL
        );
        CREATE TABLE AppState (
            Id INTEGER NOT NULL PRIMARY KEY CHECK (Id = 1),
            ProfileId TEXT NOT NULL,
            StartupCount INTEGER NOT NULL CHECK (StartupCount >= 0),
            CreatedUtc TEXT NOT NULL
        );
        """;

    private static readonly string InitialChecksum = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(InitialMigration)));

    public Task<StoreState> InitializeAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Initialize(cancellationToken), cancellationToken);

    private StoreState Initialize(CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(paths.DataDirectory);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.DatabasePath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
                DefaultTimeout = 5
            }.ToString());
            connection.Open();
            Execute(connection, null, "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
            var version = Convert.ToInt32(Scalar(connection, null, "PRAGMA user_version;"), CultureInfo.InvariantCulture);
            if (version > CurrentSchemaVersion)
            {
                throw new StoreInitializationException("store.newer_schema");
            }

            var integrity = Convert.ToString(Scalar(connection, null, "PRAGMA quick_check;"), CultureInfo.InvariantCulture);
            if (integrity != "ok")
            {
                throw new StoreInitializationException("store.corrupt");
            }

            if (version == 0 && Convert.ToInt64(Scalar(connection, null,
                "SELECT count(*) FROM sqlite_master WHERE name NOT LIKE 'sqlite_%';"), CultureInfo.InvariantCulture) != 0)
            {
                throw new StoreInitializationException("store.invalid_schema");
            }

            cancellationToken.ThrowIfCancellationRequested();
            var journalMode = Convert.ToString(Scalar(connection, null, "PRAGMA journal_mode=WAL;"), CultureInfo.InvariantCulture);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                throw new StoreInitializationException("store.unavailable");
            }

            Execute(connection, null, "PRAGMA synchronous=FULL;");
            using var transaction = connection.BeginTransaction();
            if (version == 0)
            {
                Execute(connection, transaction, InitialMigration);
                using var migration = Command(connection, transaction,
                    "INSERT INTO SchemaMigration(Version,Checksum,AppliedUtc) VALUES(1,$checksum,$utc);");
                migration.Parameters.AddWithValue("$checksum", InitialChecksum);
                migration.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                migration.ExecuteNonQuery();
                using var state = Command(connection, transaction,
                    "INSERT INTO AppState(Id,ProfileId,StartupCount,CreatedUtc) VALUES(1,$id,0,$utc);");
                state.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
                state.Parameters.AddWithValue("$utc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
                state.ExecuteNonQuery();
                Execute(connection, transaction, "PRAGMA user_version=1;");
            }

            ValidateMigration(connection, transaction);
            cancellationToken.ThrowIfCancellationRequested();
            if (Execute(connection, transaction,
                "UPDATE AppState SET StartupCount=StartupCount+1 WHERE Id=1 AND StartupCount<2147483647;") != 1)
            {
                throw new StoreInitializationException("store.invalid_schema");
            }

            var startupCount = Convert.ToInt32(Scalar(connection, transaction,
                "SELECT StartupCount FROM AppState WHERE Id=1;"), CultureInfo.InvariantCulture);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return new StoreState(CurrentSchemaVersion, startupCount);
        }
        catch (SqliteException exception)
        {
            throw new StoreInitializationException(exception.SqliteErrorCode is 11 or 26
                ? "store.corrupt"
                : "store.unavailable");
        }
        catch (IOException)
        {
            throw new StoreInitializationException("store.unavailable");
        }
        catch (UnauthorizedAccessException)
        {
            throw new StoreInitializationException("store.unavailable");
        }
    }

    private static void ValidateMigration(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = Command(connection, transaction,
            "SELECT Version,Checksum FROM SchemaMigration ORDER BY Version;");
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != CurrentSchemaVersion
            || !string.Equals(reader.GetString(1), InitialChecksum, StringComparison.Ordinal)
            || reader.Read())
        {
            throw new StoreInitializationException("store.migration_mismatch");
        }
    }

    private static SqliteCommand Command(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = 5;
        command.CommandText = sql;
        return command;
    }

    private static int Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, SqliteTransaction? transaction, string sql)
    {
        using var command = Command(connection, transaction, sql);
        return command.ExecuteScalar();
    }
}
