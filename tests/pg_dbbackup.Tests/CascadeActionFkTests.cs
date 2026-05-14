using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Foreign keys with ON DELETE CASCADE / ON DELETE SET NULL change
/// semantics on the restored DB. The action mode must be preserved on
/// pg_constraint.confdeltype.
/// </summary>
public sealed class CascadeActionFkTests
{
    private readonly PgContainerFixture _pg;

    public CascadeActionFkTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task On_Delete_Cascade_And_Set_Null_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE c_parent(id int PRIMARY KEY);" +
            "CREATE TABLE c_child_cas(" +
            "  id int PRIMARY KEY," +
            "  p int REFERENCES c_parent(id) ON DELETE CASCADE);" +
            "CREATE TABLE c_child_null(" +
            "  id int PRIMARY KEY," +
            "  p int REFERENCES c_parent(id) ON DELETE SET NULL);" +
            "INSERT INTO c_parent VALUES (1), (2);" +
            "INSERT INTO c_child_cas VALUES (10, 1), (11, 2);" +
            "INSERT INTO c_child_null VALUES (20, 1), (21, 2);");

        var full = Helpers.BackupPath("cas");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "cas_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);

            // Delete parent id=1: cascade child rows + null-out null child rows.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "DELETE FROM c_parent WHERE id = 1";
                await c.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM c_child_cas WHERE p = 1";
                Assert.Equal(0L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT p FROM c_child_null WHERE id = 20";
                var v = await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(v is DBNull,
                    "SET NULL must null out the child's FK column");
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
