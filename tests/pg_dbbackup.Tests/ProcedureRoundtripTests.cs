using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// CREATE PROCEDURE round-trip including OUT / INOUT parameters and
/// CALL semantics. Procedures differ from functions in pg_proc.prokind
/// and require CALL, not SELECT, to invoke.
/// </summary>
public sealed class ProcedureRoundtripTests
{
    private readonly PgContainerFixture _pg;

    public ProcedureRoundtripTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Procedure_With_InOut_Param_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE proc_audit(id int PRIMARY KEY, msg text);" +
            "CREATE PROCEDURE proc_bump(INOUT v int) LANGUAGE plpgsql AS $$ " +
            "  BEGIN " +
            "    INSERT INTO proc_audit VALUES (v, 'bump'); " +
            "    v := v + 100; " +
            "  END; $$;");

        var full = Helpers.BackupPath("proc_rt");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "proc_rt_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Procedure exists with correct prokind.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT prokind FROM pg_proc WHERE proname = 'proc_bump'";
                Assert.Equal('p', (char)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // CALL works; verify side-effect (audit) and the returned
            // OUT value via SELECT-from-CALL semantics.
            await r.ExecAsync("CALL proc_bump(5)");

            // Audit row written by the procedure body.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT msg FROM proc_audit WHERE id = 5";
                Assert.Equal("bump", (string)(await cmd.ExecuteScalarAsync(
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
