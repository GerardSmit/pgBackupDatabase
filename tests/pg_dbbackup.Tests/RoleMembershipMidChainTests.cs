using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Role membership (GRANT role TO role) replayed across a FULL+LOG chain.
/// Roles are cluster-scoped; only membership changes that touch ownership
/// or schema-bound ACLs end up in per-database DDL. The membership
/// itself must still exist on the cluster after restore.
/// </summary>
public sealed class RoleMembershipMidChainTests
{
    private readonly PgContainerFixture _pg;

    public RoleMembershipMidChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task ObjectGrants_To_Role_Member_RoundTrip()
    {
        var member = "rmm_member_" + Guid.NewGuid().ToString("N")[..6];
        var group = "rmm_group_" + Guid.NewGuid().ToString("N")[..6];
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync(
                $"DO $$ BEGIN " +
                $"IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{group}') " +
                $"  THEN CREATE ROLE {group}; END IF; " +
                $"IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{member}') " +
                $"  THEN CREATE ROLE {member}; END IF; " +
                $"END $$;" +
                $"GRANT {group} TO {member};");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            $"CREATE TABLE rmm_t(id int PRIMARY KEY);" +
            $"GRANT SELECT ON rmm_t TO {group};");

        var full = Helpers.BackupPath("rmm");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "rmm_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Group still has SELECT on the table.
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT count(*) FROM information_schema.role_table_grants " +
                "WHERE table_name = 'rmm_t' AND grantee = @g " +
                "  AND privilege_type = 'SELECT'";
            cmd.Parameters.AddWithValue("g", group);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
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
