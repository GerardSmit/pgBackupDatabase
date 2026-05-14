using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class TriggerEdgeCasesTests
{
    private readonly PgContainerFixture _pg;

    public TriggerEdgeCasesTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Multiple_After_Triggers_Order_Preserved()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE log_tbl(seq int GENERATED ALWAYS AS IDENTITY PRIMARY KEY, " +
            "  tag text NOT NULL);" +
            "CREATE TABLE tg_t(id int PRIMARY KEY);" +
            "CREATE FUNCTION log_a() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "BEGIN INSERT INTO log_tbl(tag) VALUES ('A'); RETURN NEW; END $$;" +
            "CREATE FUNCTION log_b() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "BEGIN INSERT INTO log_tbl(tag) VALUES ('B'); RETURN NEW; END $$;" +
            "CREATE FUNCTION log_c() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "BEGIN INSERT INTO log_tbl(tag) VALUES ('C'); RETURN NEW; END $$;" +
            "CREATE TRIGGER trg_b AFTER INSERT ON tg_t " +
            "  FOR EACH ROW EXECUTE FUNCTION log_b();" +
            "CREATE TRIGGER trg_a AFTER INSERT ON tg_t " +
            "  FOR EACH ROW EXECUTE FUNCTION log_a();" +
            "CREATE TRIGGER trg_c AFTER INSERT ON tg_t " +
            "  FOR EACH ROW EXECUTE FUNCTION log_c();");

        var full = Helpers.BackupPath("trg_order");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "trg_order_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await r.ExecAsync("INSERT INTO tg_t VALUES (1);");
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT array_agg(tag ORDER BY seq) FROM log_tbl";
            var tags = (string[])(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal(new[] { "A", "B", "C" }, tags);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Disabled_Trigger_State_Preserved()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE dtg_t(id int PRIMARY KEY);" +
            "CREATE TABLE dtg_log(id int PRIMARY KEY);" +
            "CREATE FUNCTION dtg_fn() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "BEGIN INSERT INTO dtg_log VALUES (NEW.id); RETURN NEW; END $$;" +
            "CREATE TRIGGER dtg AFTER INSERT ON dtg_t " +
            "  FOR EACH ROW EXECUTE FUNCTION dtg_fn();" +
            "ALTER TABLE dtg_t DISABLE TRIGGER dtg;");

        var full = Helpers.BackupPath("dtg");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "dtg_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT tgenabled FROM pg_trigger " +
                "WHERE tgrelid = 'dtg_t'::regclass AND NOT tgisinternal";
            var v = (char)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal('D', v);

            await r.ExecAsync("INSERT INTO dtg_t VALUES (1);");
            await using var c2 = r.CreateCommand();
            c2.CommandText = "SELECT count(*) FROM dtg_log";
            Assert.Equal(0L, (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Constraint_Trigger_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE ctg_t(id int PRIMARY KEY, v int NOT NULL);" +
            "CREATE FUNCTION ctg_fn() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "BEGIN IF NEW.v < 0 THEN RAISE EXCEPTION 'neg'; END IF; " +
            "  RETURN NEW; END $$;" +
            "CREATE CONSTRAINT TRIGGER ctg AFTER INSERT ON ctg_t " +
            "  DEFERRABLE INITIALLY DEFERRED " +
            "  FOR EACH ROW EXECUTE FUNCTION ctg_fn();");

        var full = Helpers.BackupPath("ctg");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "ctg_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT tgconstraint <> 0, tgdeferrable, tginitdeferred " +
                "FROM pg_trigger " +
                "WHERE tgname = 'ctg' AND tgrelid = 'ctg_t'::regclass";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(rdr.GetBoolean(0));
            Assert.True(rdr.GetBoolean(1));
            Assert.True(rdr.GetBoolean(2));
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
