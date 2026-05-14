using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class DomainAndExtensionTypesTests
{
    private readonly PgContainerFixture _pg;

    public DomainAndExtensionTypesTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Domain_Check_Enforced_After_FullLog_Restore()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE DOMAIN posint AS int CHECK (VALUE > 0);" +
            "CREATE TABLE dc_t(id int PRIMARY KEY, v posint NOT NULL);" +
            "INSERT INTO dc_t VALUES (1, 10);");

        var full = Helpers.BackupPath("dc_full");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync("INSERT INTO dc_t VALUES (2, 20);");

        var log = Helpers.BackupPath("dc_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "dc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "INSERT INTO dc_t VALUES (3, -1)";
            var ex = await Assert.ThrowsAsync<PostgresException>(
                () => cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken));
            Assert.Equal("23514", ex.SqlState);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Custom_Operator_And_Class_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE FUNCTION my_int_eq(int, int) RETURNS bool " +
            "  LANGUAGE sql IMMUTABLE AS 'SELECT $1 = $2';" +
            "CREATE OPERATOR ==== (LEFTARG = int, RIGHTARG = int, " +
            "  FUNCTION = my_int_eq, COMMUTATOR = ====);");

        var full = Helpers.BackupPath("op");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "op_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_operator WHERE oprname = '===='";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Custom_Cast_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE FUNCTION text_to_int_strict(text) RETURNS int " +
            "  LANGUAGE sql IMMUTABLE AS 'SELECT $1::int';" +
            "CREATE CAST (text AS int) WITH FUNCTION text_to_int_strict(text) " +
            "  AS ASSIGNMENT;");

        var full = Helpers.BackupPath("cast");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "cast_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_cast c " +
                "JOIN pg_proc p ON p.oid = c.castfunc " +
                "WHERE p.proname = 'text_to_int_strict'";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task PgTrgm_Citext_Hstore_Ltree_Xml_Types_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE EXTENSION pg_trgm;" +
            "CREATE EXTENSION citext;" +
            "CREATE EXTENSION hstore;" +
            "CREATE EXTENSION ltree;" +
            "CREATE TABLE etypes(" +
            "  id int PRIMARY KEY," +
            "  ci citext NOT NULL," +
            "  h hstore NOT NULL," +
            "  lt ltree NOT NULL," +
            "  x xml NOT NULL," +
            "  search text NOT NULL);" +
            "INSERT INTO etypes VALUES (" +
            "  1, 'Hello', 'a=>1,b=>2', 'Top.Animal.Cat', " +
            "  '<r><a>1</a></r>'::xml, 'hello world');" +
            "CREATE INDEX ON etypes USING GIN(search gin_trgm_ops);");

        var full = Helpers.BackupPath("etypes");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "etypes_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT ci::text, h->'a', lt::text, " +
                "       (xpath('/r/a/text()', x))[1]::text FROM etypes";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal("Hello", rdr.GetString(0));
            Assert.Equal("1", rdr.GetString(1));
            Assert.Equal("Top.Animal.Cat", rdr.GetString(2));
            Assert.Equal("1", rdr.GetString(3));
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, params string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
