using FileOrganizer.Application;
using FileOrganizer.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace FileOrganizer.Tests;

public sealed class StoreTests
{
    [Fact]
    public async Task RestartPreservesProfileAndAdvancesStartupWithoutReapplyingSchema()
    {
        var paths = NewPaths();
        var first = await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        var profile = Scalar(paths, "SELECT ProfileId FROM AppState;");
        var checksum = Scalar(paths, "SELECT Checksum FROM SchemaMigration;");
        var second = await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);

        Assert.Equal(new StoreState(1, 1), first);
        Assert.Equal(new StoreState(1, 2), second);
        Assert.Equal(profile, Scalar(paths, "SELECT ProfileId FROM AppState;"));
        Assert.Equal(checksum, Scalar(paths, "SELECT Checksum FROM SchemaMigration;"));
        Assert.Equal(1L, Scalar(paths, "SELECT count(*) FROM SchemaMigration;"));
    }

    [Fact]
    public async Task NewerSchemaIsRefusedWithoutChangingStartupState()
    {
        var paths = NewPaths();
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        Execute(paths, "PRAGMA user_version=999;");

        var error = await Assert.ThrowsAsync<StoreInitializationException>(() =>
            new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None));

        Assert.Equal("store.newer_schema", error.ErrorCode);
        Assert.Equal(999L, Scalar(paths, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar(paths, "SELECT StartupCount FROM AppState;"));
    }

    [Fact]
    public async Task ChangedMigrationChecksumIsRefusedWithoutOverwritingEvidence()
    {
        var paths = NewPaths();
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        Execute(paths, "UPDATE SchemaMigration SET Checksum='fixture-tamper';");

        var error = await Assert.ThrowsAsync<StoreInitializationException>(() =>
            new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None));

        Assert.Equal("store.migration_mismatch", error.ErrorCode);
        Assert.Equal("fixture-tamper", Scalar(paths, "SELECT Checksum FROM SchemaMigration;"));
        Assert.Equal(1L, Scalar(paths, "SELECT StartupCount FROM AppState;"));
    }

    [Fact]
    public async Task FailedStartupRollsBackTransactionAndCanBeRetried()
    {
        var paths = NewPaths();
        await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        // RAISE(FAIL) preserves trigger effects within the statement, so only transaction rollback
        // prevents the probe write surviving this injected startup failure.
        Execute(paths, """
            CREATE TABLE FailureProbe(Value INTEGER);
            CREATE TRIGGER FailStartup BEFORE UPDATE ON AppState BEGIN
                INSERT INTO FailureProbe VALUES(1);
                SELECT RAISE(FAIL,'injected fixture failure');
            END;
            """);

        await Assert.ThrowsAsync<StoreInitializationException>(() =>
            new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None));

        Assert.Equal(0L, Scalar(paths, "SELECT count(*) FROM FailureProbe;"));
        Assert.Equal(1L, Scalar(paths, "SELECT StartupCount FROM AppState;"));
        Execute(paths, "DROP TRIGGER FailStartup;");
        var retried = await new SqliteApplicationStore(paths).InitializeAsync(CancellationToken.None);
        Assert.Equal(2, retried.StartupCount);
        Assert.Equal("ok", Scalar(paths, "PRAGMA integrity_check;"));
    }

    [Fact]
    public async Task CancelledInitializationDoesNotCreateDatabase()
    {
        var paths = NewPaths();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new SqliteApplicationStore(paths).InitializeAsync(cancellation.Token));
        Assert.False(File.Exists(paths.DatabasePath));
    }

    private static AppDataPaths NewPaths() => new(new TestWorkspace().Root);

    private static object? Scalar(AppDataPaths paths, string sql)
    {
        using var connection = Open(paths);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(AppDataPaths paths, string sql)
    {
        using var connection = Open(paths);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(AppDataPaths paths)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }
}
