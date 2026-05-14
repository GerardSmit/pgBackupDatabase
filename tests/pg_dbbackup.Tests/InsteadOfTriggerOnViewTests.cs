using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// INSTEAD OF triggers on views: the only legal trigger timing on views.
/// Tests that an updatable view backed by an INSTEAD OF INSERT trigger
/// survives restore intact.
/// </summary>
public sealed class InsteadOfTriggerOnViewTests
{
    private readonly PgContainerFixture _pg;

    public InsteadOfTriggerOnViewTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task InsteadOf_Insert_Trigger_On_View_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE iot_base(id int PRIMARY KEY, label text NOT NULL);" +
            "INSERT INTO iot_base VALUES (1, 'one');" +
            "CREATE VIEW iot_v AS SELECT id, label FROM iot_base;" +
            "CREATE FUNCTION iot_insert_fn() RETURNS trigger LANGUAGE plpgsql AS $$ " +
            "  BEGIN " +
            "    INSERT INTO iot_base(id, label) " +
            "    VALUES (NEW.id, upper(NEW.label)); " +
            "    RETURN NEW; " +
            "  END; $$;" +
            "CREATE TRIGGER iot_insert_trg INSTEAD OF INSERT ON iot_v " +
            "  FOR EACH ROW EXECUTE FUNCTION iot_insert_fn();");

        var full = Helpers.BackupPath("iot_view");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "iot_view_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Trigger exists and is correctly typed.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT tgname, tgtype, tgenabled, c.relkind " +
                    "FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid " +
                    "WHERE t.tgname = 'iot_insert_trg'";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal("iot_insert_trg", rdr.GetString(0));
                Assert.Equal('v', rdr.GetChar(3));
            }

            // INSERT through the view fires INSTEAD OF and writes to base.
            await r.ExecAsync("INSERT INTO iot_v VALUES (2, 'two')");

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT label FROM iot_base WHERE id = 2";
                Assert.Equal("TWO", (string)(await cmd.ExecuteScalarAsync(
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
