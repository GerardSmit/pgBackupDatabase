using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class MetadataDdlTests
{
    private readonly PgContainerFixture _pg;

    public MetadataDdlTests(PgContainerFixture pg) => _pg = pg;

    private static async Task<string> GetMetadataAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_test_metadata(@db)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        var result = await cmd.ExecuteScalarAsync();
        return (string)result!;
    }

    private static async Task<string> GetDdlAsync(NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_test_ddl(@db)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        var result = await cmd.ExecuteScalarAsync();
        return (string)result!;
    }

    private static async Task ExecAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Metadata_Includes_Extensions()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await ExecAsync(conn, "CREATE EXTENSION pgcrypto");

        var output = await GetMetadataAsync(conn);

        Assert.Contains("CREATE EXTENSION", output);
        Assert.Contains("pgcrypto", output);
    }

    [Fact]
    public async Task Ddl_Includes_User_Tables()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await ExecAsync(conn,
            "CREATE TABLE widgets(id int PRIMARY KEY, name text, qty int DEFAULT 0)");

        var output = await GetDdlAsync(conn);

        Assert.Contains("CREATE TABLE", output);
        Assert.Contains("widgets", output);
        Assert.Contains("qty", output);
    }

    [Fact]
    public async Task Ddl_Includes_Foreign_Key()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await ExecAsync(conn, "CREATE TABLE parents(id int PRIMARY KEY)");
        await ExecAsync(conn,
            "CREATE TABLE children(id int PRIMARY KEY, parent_id int REFERENCES parents(id))");

        var output = await GetDdlAsync(conn);

        Assert.Contains("FOREIGN KEY", output);
        Assert.Contains("parents", output);
    }

    [Fact]
    public async Task Ddl_Excludes_Dbbackup_Schema()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();

        var output = await GetDdlAsync(conn);

        Assert.DoesNotContain("dbbackup.db_config", output);
        Assert.DoesNotContain("\"dbbackup\".\"db_config\"", output);
    }

    [Fact]
    public async Task Ddl_Includes_Index()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        await ExecAsync(conn, "CREATE TABLE items(id int PRIMARY KEY, name text)");
        await ExecAsync(conn, "CREATE INDEX items_name_idx ON items USING btree (name)");

        var output = await GetDdlAsync(conn);

        Assert.Contains("items_name_idx", output);
        Assert.Contains("CREATE INDEX", output);
    }

    [Fact]
    public async Task Metadata_Includes_Schema_Grant()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var dbName = conn.Database!;
        var roleName = "testrole_" + Guid.NewGuid().ToString("N")[..8];

        await ExecAsync(conn, $"CREATE ROLE \"{roleName}\"");
        try
        {
            await ExecAsync(conn,
                $"GRANT CONNECT ON DATABASE \"{dbName}\" TO \"{roleName}\"");

            var output = await GetMetadataAsync(conn);

            Assert.Contains(roleName, output);
            Assert.Contains("GRANT", output);
        }
        finally
        {
            await ExecAsync(conn,
                $"REVOKE ALL ON DATABASE \"{dbName}\" FROM \"{roleName}\"");
            await ExecAsync(conn, $"DROP ROLE \"{roleName}\"");
        }
    }
}
