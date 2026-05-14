using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Statement-level trigger with REFERENCING NEW TABLE / OLD TABLE
/// transition tables. Transition tables are stored in tgnewtable /
/// tgoldtable on pg_trigger; they must survive restore.
/// </summary>
public sealed class TriggerTransitionTablesTests
{
    private readonly PgContainerFixture _pg;

    public TriggerTransitionTablesTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Transition_Table_Trigger_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE tt_t(id int PRIMARY KEY, v int);" +
            "CREATE TABLE tt_log(inserted_count int);" +
            "CREATE FUNCTION tt_fn() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "  BEGIN " +
            "    INSERT INTO tt_log " +
            "    SELECT count(*) FROM new_rows; " +
            "    RETURN NULL; " +
            "  END; $$;" +
            "CREATE TRIGGER tt_trg AFTER INSERT ON tt_t " +
            "  REFERENCING NEW TABLE AS new_rows " +
            "  FOR EACH STATEMENT EXECUTE FUNCTION tt_fn();");

        var full = Helpers.BackupPath("tt");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "tt_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // The trigger has tgnewtable set.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT tgnewtable FROM pg_trigger WHERE tgname = 'tt_trg'";
                Assert.Equal("new_rows", (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Multi-row insert fires the statement-level trigger once and
            // the transition table reports the correct count.
            await r.ExecAsync(
                "INSERT INTO tt_t VALUES (1,1),(2,2),(3,3),(4,4),(5,5);");
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*), sum(inserted_count) FROM tt_log";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(1L, rdr.GetInt64(0));
                Assert.Equal(5L, rdr.GetInt64(1));
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
