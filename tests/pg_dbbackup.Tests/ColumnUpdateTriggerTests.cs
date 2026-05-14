using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// CREATE TRIGGER ... AFTER UPDATE OF (col1, col2) ... — column-scoped
/// triggers. tgattr in pg_trigger encodes the watched columns and must
/// survive restore.
/// </summary>
public sealed class ColumnUpdateTriggerTests
{
    private readonly PgContainerFixture _pg;

    public ColumnUpdateTriggerTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task UpdateOf_Trigger_Captures_Column_Set()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE cu_t(id int PRIMARY KEY, a int, b int, c int);" +
            "CREATE TABLE cu_log(at_id int, ts timestamptz NOT NULL DEFAULT now());" +
            "CREATE FUNCTION cu_fn() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "  BEGIN INSERT INTO cu_log(at_id) VALUES (NEW.id); RETURN NEW; END; $$;" +
            "CREATE TRIGGER cu_trg AFTER UPDATE OF a, b ON cu_t " +
            "  FOR EACH ROW EXECUTE FUNCTION cu_fn();");

        var full = Helpers.BackupPath("cu");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "cu_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Verify the trigger's column set matches a + b (by attnum).
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT array_agg(a.attname ORDER BY a.attname) " +
                    "FROM pg_trigger t " +
                    "JOIN pg_attribute a " +
                    "  ON a.attrelid = t.tgrelid AND a.attnum = ANY(t.tgattr) " +
                    "WHERE t.tgname = 'cu_trg'";
                var arr = (string[])(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { "a", "b" }, arr);
            }

            // Update of `c` only — trigger must NOT fire.
            await r.ExecAsync("INSERT INTO cu_t VALUES (1, 1, 2, 3);");
            await r.ExecAsync("UPDATE cu_t SET c = 99 WHERE id = 1");
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM cu_log";
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Update of `a` — trigger MUST fire.
            await r.ExecAsync("UPDATE cu_t SET a = 10 WHERE id = 1");
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM cu_log";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
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
