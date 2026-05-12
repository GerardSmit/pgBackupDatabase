using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class ModeConfigTests
{
    private readonly PgContainerFixture _pg;

    public ModeConfigTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task DefaultMode_IsSimple()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_get_mode(@db)";
        cmd.Parameters.AddWithValue("db", conn.Database!);

        var mode = (string?)await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("simple", mode);
    }

    [Fact]
    public async Task SetMode_Full_Persists()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = conn.Database!;

        await using (var set = conn.CreateCommand())
        {
            set.CommandText = "SELECT dbbackup.pg_dbbackup_set_mode(@db, @mode)";
            set.Parameters.AddWithValue("db", dbName);
            set.Parameters.AddWithValue("mode", "full");
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var get = conn.CreateCommand();
        get.CommandText = "SELECT dbbackup.pg_dbbackup_get_mode(@db)";
        get.Parameters.AddWithValue("db", dbName);

        var mode = (string?)await get.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        Assert.Equal("full", mode);
    }

    [Fact]
    public async Task Log_Type_Rejected_On_Simple_Mode()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = conn.Database!;

        await using (var ensure = conn.CreateCommand())
        {
            ensure.CommandText = "SELECT dbbackup.pg_dbbackup_set_mode(@db, 'simple')";
            ensure.Parameters.AddWithValue("db", dbName);
            await ensure.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'log')";
        cmd.Parameters.AddWithValue("db", dbName);
        cmd.Parameters.AddWithValue("path", "/tmp/dummy.bak");

        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_Mode_Rejected()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_set_mode(@db, 'bogus')";
        cmd.Parameters.AddWithValue("db", conn.Database!);

        await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }
}
