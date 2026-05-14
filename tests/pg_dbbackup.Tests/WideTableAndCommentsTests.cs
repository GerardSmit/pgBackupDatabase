using System.Text;
using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class WideTableAndCommentsTests
{
    private readonly PgContainerFixture _pg;

    public WideTableAndCommentsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Table_With_1000_Columns_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var cols = new StringBuilder("id int PRIMARY KEY");
        for (int i = 1; i <= 1000; i++)
            cols.Append($", c{i} int NOT NULL DEFAULT {i}");
        await src.ExecAsync($"CREATE TABLE wide_t({cols});", timeoutSeconds: 120);
        await src.ExecAsync("INSERT INTO wide_t(id) VALUES (1);",
            timeoutSeconds: 120);

        var full = Helpers.BackupPath("wide");
        await src.BackupFullAsync(full, compress: true,
            commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = "wide_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_attribute " +
                "WHERE attrelid = 'wide_t'::regclass " +
                "  AND attnum > 0 AND NOT attisdropped";
            Assert.Equal(1001L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
            await using var c2 = r.CreateCommand();
            c2.CommandText = "SELECT c500 + c777 FROM wide_t WHERE id = 1";
            Assert.Equal(500 + 777, (int)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Comments_On_All_Object_Kinds_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var srcDb = src.Database!;
        await src.ExecAsync(
            "CREATE SCHEMA s1;" +
            "COMMENT ON SCHEMA s1 IS 'schema comment';" +
            "CREATE TYPE s1.color AS ENUM ('r','g');" +
            "COMMENT ON TYPE s1.color IS 'type comment';" +
            "CREATE TABLE s1.t(id int PRIMARY KEY, name text NOT NULL);" +
            "COMMENT ON TABLE s1.t IS 'table comment';" +
            "COMMENT ON COLUMN s1.t.name IS 'column comment';" +
            "CREATE FUNCTION s1.fn() RETURNS int LANGUAGE sql AS 'SELECT 1';" +
            "COMMENT ON FUNCTION s1.fn() IS 'function comment';");
        await using (var admin = await _pg.AdminAsync())
        await using (var cmd = admin.CreateCommand())
        {
            cmd.CommandText =
                $"COMMENT ON DATABASE \"{srcDb}\" IS 'db comment'";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var full = Helpers.BackupPath("comments");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "comments_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT obj_description(('s1'::regnamespace)::oid, 'pg_namespace'), " +
                    "       obj_description('s1.color'::regtype::oid, 'pg_type'), " +
                    "       obj_description('s1.t'::regclass), " +
                    "       col_description('s1.t'::regclass, " +
                    "         (SELECT attnum FROM pg_attribute " +
                    "          WHERE attrelid='s1.t'::regclass AND attname='name')), " +
                    "       obj_description('s1.fn()'::regprocedure::oid, 'pg_proc')";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal("schema comment", rdr.GetString(0));
                Assert.Equal("type comment", rdr.GetString(1));
                Assert.Equal("table comment", rdr.GetString(2));
                Assert.Equal("column comment", rdr.GetString(3));
                Assert.Equal("function comment", rdr.GetString(4));
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task AlterDefaultPrivileges_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync(
                "DO $$ BEGIN " +
                "IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='adp_reader') " +
                "THEN CREATE ROLE adp_reader; END IF; END $$;");
        }
        await src.ExecAsync(
            "ALTER DEFAULT PRIVILEGES IN SCHEMA public " +
            "  GRANT SELECT ON TABLES TO adp_reader;" +
            "CREATE TABLE adp_t(id int PRIMARY KEY);");

        var full = Helpers.BackupPath("adp");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "adp_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_default_acl d " +
                "JOIN pg_namespace n ON n.oid = d.defaclnamespace " +
                "WHERE n.nspname = 'public' " +
                "  AND array_to_string(d.defaclacl, ',') LIKE '%adp_reader%'";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Temporary_Tables_Excluded_From_Backup()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        var setupConn = src;
        await setupConn.ExecAsync(
            "CREATE TABLE keep_t(id int PRIMARY KEY);" +
            "INSERT INTO keep_t VALUES (1);" +
            "CREATE TEMP TABLE tmp_t(id int PRIMARY KEY);" +
            "INSERT INTO tmp_t VALUES (99);");

        var full = Helpers.BackupPath("tmp_excl");
        await setupConn.BackupFullAsync(full, compress: true);
        await setupConn.CloseAsync();

        var target = "tmp_excl_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_class c " +
                "JOIN pg_namespace n ON c.relnamespace = n.oid " +
                "WHERE c.relname = 'tmp_t'";
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
            await using var c2 = r.CreateCommand();
            c2.CommandText = "SELECT count(*) FROM keep_t";
            Assert.Equal(1L, (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string file)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", new[] { file });
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
