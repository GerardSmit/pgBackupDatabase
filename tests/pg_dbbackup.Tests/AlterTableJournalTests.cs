using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class AlterTableJournalTests
{
    private readonly PgContainerFixture _pg;

    public AlterTableJournalTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task AlterTable_AddSetColumns_Replay_Through_Log()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE alter_t(id int PRIMARY KEY, a text);" +
            "INSERT INTO alter_t VALUES (1, 'one');");

        var full = Helpers.BackupPath("alter_cols");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync(
            "ALTER TABLE alter_t ADD COLUMN c text DEFAULT 'd';" +
            "ALTER TABLE alter_t ALTER COLUMN a SET NOT NULL;" +
            "ALTER TABLE alter_t ALTER COLUMN a SET DEFAULT 'def';" +
            "INSERT INTO alter_t(id) VALUES (2);");

        var log = Helpers.BackupPath("alter_cols_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "alter_cols_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT attname FROM pg_attribute " +
                    "WHERE attrelid = 'alter_t'::regclass " +
                    "  AND attnum > 0 AND NOT attisdropped ORDER BY attnum";
                var cols = new List<string>();
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                    cols.Add(rdr.GetString(0));
                Assert.Equal(new[] { "id", "a", "c" }, cols);
            }

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT a, c FROM alter_t WHERE id = 2";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal("def", rdr.GetString(0));
                Assert.Equal("d", rdr.GetString(1));
            }

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT attnotnull FROM pg_attribute " +
                    "WHERE attrelid = 'alter_t'::regclass AND attname = 'a'";
                Assert.True((bool)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task AlterTable_DropColumn_Replays_Through_Log()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE drop_t(id int PRIMARY KEY, keep text, gone text);" +
            "INSERT INTO drop_t VALUES (1, 'k', 'g');");

        var full = Helpers.BackupPath("drop_col");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync("ALTER TABLE drop_t DROP COLUMN gone;");

        var log = Helpers.BackupPath("drop_col_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "drop_col_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_attribute " +
                "WHERE attrelid = 'drop_t'::regclass " +
                "  AND attname = 'gone' AND NOT attisdropped";
            Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task AttachDetachPartition_Through_Journal()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE part_t(day date NOT NULL, id int NOT NULL, " +
            "  PRIMARY KEY(day, id)) PARTITION BY RANGE(day);" +
            "CREATE TABLE part_jan PARTITION OF part_t " +
            "  FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');" +
            "CREATE TABLE part_feb_orphan(day date NOT NULL, id int NOT NULL, " +
            "  PRIMARY KEY(day, id));" +
            "INSERT INTO part_t VALUES ('2026-01-15', 1);" +
            "INSERT INTO part_feb_orphan VALUES ('2026-02-15', 2);");

        var full = Helpers.BackupPath("part_attach");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync(
            "ALTER TABLE part_t ATTACH PARTITION part_feb_orphan " +
            "  FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');" +
            "ALTER TABLE part_t DETACH PARTITION part_jan;");

        var log = Helpers.BackupPath("part_attach_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "part_attach_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM pg_inherits " +
                "WHERE inhparent = 'part_t'::regclass";
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);

            await using var cmd2 = r.CreateCommand();
            cmd2.CommandText = "SELECT count(*) FROM part_t";
            Assert.Equal(1L, (long)(await cmd2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Default_Partition_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE def_part(id int NOT NULL, region text NOT NULL, " +
            "  PRIMARY KEY(id, region)) PARTITION BY LIST(region);" +
            "CREATE TABLE def_part_us PARTITION OF def_part FOR VALUES IN ('us');" +
            "CREATE TABLE def_part_other PARTITION OF def_part DEFAULT;" +
            "INSERT INTO def_part VALUES (1, 'us'), (2, 'eu'), (3, 'jp');");

        var full = Helpers.BackupPath("def_part");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "def_part_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM def_part_other";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT pg_get_expr(c.relpartbound, c.oid) " +
                    "FROM pg_class c WHERE c.relname = 'def_part_other'";
                var bound = (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal("DEFAULT", bound);
            }
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
