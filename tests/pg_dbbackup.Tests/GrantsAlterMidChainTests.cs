using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// GRANT / REVOKE replay across a FULL+LOG chain. ACL changes done
/// during the LOG window must be reflected after restore via the DDL
/// journal capture.
/// </summary>
public sealed class GrantsAlterMidChainTests
{
    private readonly PgContainerFixture _pg;

    public GrantsAlterMidChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Grant_Revoke_During_Log_Window_Reflected_After_Restore()
    {
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync(
                "DO $$ BEGIN " +
                "IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='gma_alice') " +
                "  THEN CREATE ROLE gma_alice; END IF; " +
                "IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='gma_bob') " +
                "  THEN CREATE ROLE gma_bob; END IF; " +
                "END $$;");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE gma_t(id int PRIMARY KEY, v text);" +
            "GRANT SELECT ON gma_t TO gma_alice;" +
            "INSERT INTO gma_t VALUES (1,'a');");

        var full = Helpers.BackupPath("gma_full");
        await src.BackupFullAsync(full);

        // GRANT/REVOKE during the LOG window.
        await src.ExecAsync(
            "GRANT INSERT, UPDATE ON gma_t TO gma_bob;" +
            "REVOKE SELECT ON gma_t FROM gma_alice;" +
            "GRANT SELECT, UPDATE ON gma_t TO gma_alice;");

        var log1 = Helpers.BackupPath("gma_log");
        await src.BackupLogAsync(log1, basePath: full);
        await src.CloseAsync();

        var target = "gma_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log1);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT grantee, privilege_type " +
                "FROM information_schema.role_table_grants " +
                "WHERE table_name = 'gma_t' " +
                "  AND grantee IN ('gma_alice','gma_bob') " +
                "ORDER BY grantee, privilege_type";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var rows = new List<(string g, string p)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add((rdr.GetString(0), rdr.GetString(1)));

            Assert.Contains(("gma_alice", "SELECT"), rows);
            Assert.Contains(("gma_alice", "UPDATE"), rows);
            Assert.Contains(("gma_bob", "INSERT"), rows);
            Assert.Contains(("gma_bob", "UPDATE"), rows);
            // Bob never got SELECT.
            Assert.DoesNotContain(("gma_bob", "SELECT"), rows);
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
