using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Statement-level triggers (FOR EACH STATEMENT) and per-row triggers
/// with WHEN conditions. Both store extra bits in pg_trigger that need
/// to survive restore for the trigger to behave identically.
/// </summary>
public sealed class TriggerStatementAndWhenTests
{
    private readonly PgContainerFixture _pg;

    public TriggerStatementAndWhenTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Statement_Trigger_And_When_Condition_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE tw_t(id int PRIMARY KEY, v int);" +
            "CREATE TABLE tw_log(kind text, ts timestamptz NOT NULL DEFAULT now());" +
            "CREATE FUNCTION tw_stmt_fn() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "  BEGIN INSERT INTO tw_log(kind) VALUES ('stmt'); RETURN NULL; END; $$;" +
            "CREATE FUNCTION tw_row_fn() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "  BEGIN INSERT INTO tw_log(kind) VALUES ('row'); RETURN NEW; END; $$;" +
            "CREATE TRIGGER tw_stmt_trg AFTER INSERT ON tw_t " +
            "  FOR EACH STATEMENT EXECUTE FUNCTION tw_stmt_fn();" +
            "CREATE TRIGGER tw_when_trg AFTER INSERT ON tw_t " +
            "  FOR EACH ROW WHEN (NEW.v > 100) " +
            "  EXECUTE FUNCTION tw_row_fn();");

        var full = Helpers.BackupPath("tw");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "tw_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Insert two rows in one statement; v=5 (below threshold) +
            // v=200 (above threshold).
            await r.ExecAsync(
                "INSERT INTO tw_t VALUES (1, 5), (2, 200);");

            // Statement trigger fires once; row trigger fires once (v=200).
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT kind, count(*) FROM tw_log " +
                    "GROUP BY kind ORDER BY kind";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                var rows = new List<(string k, long n)>();
                while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                    rows.Add((rdr.GetString(0), rdr.GetInt64(1)));
                Assert.Equal(2, rows.Count);
                Assert.Contains(("row", 1L), rows);
                Assert.Contains(("stmt", 1L), rows);
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
