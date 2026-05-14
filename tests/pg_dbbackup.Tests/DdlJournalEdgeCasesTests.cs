using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// DDL replay edge cases through the FULL-mode chain: enum value addition,
/// column type change with USING, TRUNCATE, REINDEX, rename, cascade drop.
/// All issued AFTER the FULL backup so logical-decoded DDL must reach the
/// restored database.
/// </summary>
public sealed class DdlJournalEdgeCasesTests
{
    private readonly PgContainerFixture _pg;

    public DdlJournalEdgeCasesTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task AlterType_AddValue_Replays_To_Restored_Db()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TYPE mood AS ENUM ('sad', 'ok');" +
            "CREATE TABLE t(id int PRIMARY KEY, m mood);" +
            "INSERT INTO t VALUES (1, 'ok');");

        var full = Helpers.BackupPath("ddl_enum");
        await src.BackupFullAsync(full, compress: true);

        // ALTER TYPE ADD VALUE + use in same transaction is forbidden
        // (PG 55P04); use the value in a later, separate command.
        await src.ExecAsync("ALTER TYPE mood ADD VALUE 'happy'");
        await src.ExecAsync("INSERT INTO t VALUES (2, 'happy')");

        var log = Helpers.BackupPath("ddl_enum_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "ddl_enum_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);
            var labels = (string[])(await ExecScalarAsync(r,
                "SELECT array_agg(enumlabel ORDER BY enumsortorder) " +
                "FROM pg_enum e JOIN pg_type t ON t.oid = e.enumtypid " +
                "WHERE t.typname = 'mood'"))!;
            Assert.Equal(new[] { "sad", "ok", "happy" }, labels);

            var rowCount = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM t WHERE m = 'happy'"))!;
            Assert.Equal(1L, rowCount);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task AlterColumn_TypeChange_With_Using_Replays()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, v text);" +
            "INSERT INTO t VALUES (1, '42'), (2, '100');");

        var full = Helpers.BackupPath("ddl_alter");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync(
            "ALTER TABLE t ALTER COLUMN v TYPE int USING v::int;" +
            "UPDATE t SET v = v + 1;");

        var log = Helpers.BackupPath("ddl_alter_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "ddl_alter_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);
            var sum = (long)(await ExecScalarAsync(r,
                "SELECT sum(v)::bigint FROM t"))!;
            Assert.Equal(43L + 101L, sum);
            var typename = (string)(await ExecScalarAsync(r,
                "SELECT pg_typeof(v)::text FROM t LIMIT 1"))!;
            Assert.Equal("integer", typename);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Truncate_Replays_As_Empty_Table()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY);" +
            "INSERT INTO t SELECT generate_series(1, 50);");

        var full = Helpers.BackupPath("ddl_trunc");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync(
            "TRUNCATE t;" +
            "INSERT INTO t SELECT generate_series(100, 105);");

        var log = Helpers.BackupPath("ddl_trunc_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "ddl_trunc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);
            var rows = (long)(await ExecScalarAsync(r, "SELECT count(*) FROM t"))!;
            Assert.Equal(6L, rows);
            var maxId = (int)(await ExecScalarAsync(r, "SELECT max(id) FROM t"))!;
            Assert.Equal(105, maxId);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Object_Rename_Replays()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE old_name(id int PRIMARY KEY);" +
            "INSERT INTO old_name VALUES (1);");

        var full = Helpers.BackupPath("ddl_rename");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync(
            "ALTER TABLE old_name RENAME TO new_name;" +
            "INSERT INTO new_name VALUES (2);");

        var log = Helpers.BackupPath("ddl_rename_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "ddl_rename_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);
            var rows = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM new_name"))!;
            Assert.Equal(2L, rows);
            var oldExists = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM pg_class WHERE relname = 'old_name'"))!;
            Assert.Equal(0L, oldExists);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Cascade_Drop_Replays_Across_Dependents()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE parent(id int PRIMARY KEY);" +
            "CREATE TABLE child(id int PRIMARY KEY, pid int REFERENCES parent(id));" +
            "INSERT INTO parent VALUES (1);" +
            "INSERT INTO child VALUES (10, 1);");

        var full = Helpers.BackupPath("ddl_cascade");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync("DROP TABLE parent CASCADE;");

        var log = Helpers.BackupPath("ddl_cascade_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "ddl_cascade_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);

            // DROP TABLE parent CASCADE drops parent and the FK constraint
            // on child; child table itself remains.
            var remaining = (string)(await ExecScalarAsync(r,
                "SELECT coalesce(string_agg(relname, ',' ORDER BY relname), '') " +
                "FROM pg_class " +
                "WHERE relname IN ('parent','child') AND relkind = 'r'"))!;
            Assert.Equal("child", remaining);

            var fkCount = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM pg_constraint " +
                "WHERE contype = 'f' AND conrelid = 'child'::regclass"))!;
            Assert.Equal(0L, fkCount);

            // Child rows survive (FK constraint was the only thing CASCADE
            // touched on child).
            var childRows = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM child"))!;
            Assert.Equal(1L, childRows);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Drop_Independent_Table_Replays()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE keep_me(id int PRIMARY KEY);" +
            "CREATE TABLE drop_me(id int PRIMARY KEY);" +
            "INSERT INTO keep_me VALUES (1);" +
            "INSERT INTO drop_me VALUES (1);");

        var full = Helpers.BackupPath("ddl_drop");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync("DROP TABLE drop_me");
        await src.ExecAsync("INSERT INTO keep_me VALUES (2)");

        var log = Helpers.BackupPath("ddl_drop_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "ddl_drop_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);
            var keepRows = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM keep_me"))!;
            Assert.Equal(2L, keepRows);
            var dropExists = (long)(await ExecScalarAsync(r,
                "SELECT count(*) FROM pg_class " +
                "WHERE relname = 'drop_me' AND relkind = 'r'"))!;
            Assert.Equal(0L, dropExists);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string[] files)
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

    private static async Task<object?> ExecScalarAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
