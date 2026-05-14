using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Three-level table inheritance: grandparent → parent → child. DDL
/// emission must order INHERITS in topological order so child
/// references parent and parent references grandparent.
/// </summary>
public sealed class MultiLevelInheritanceTests
{
    private readonly PgContainerFixture _pg;

    public MultiLevelInheritanceTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Three_Level_Inheritance_Chain_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE mli_gp(id int PRIMARY KEY, gp_col text);" +
            "CREATE TABLE mli_p(p_col text) INHERITS (mli_gp);" +
            "CREATE TABLE mli_c(c_col text) INHERITS (mli_p);" +
            "INSERT INTO mli_gp VALUES (1, 'g1');" +
            "INSERT INTO mli_p VALUES (2, 'g2', 'p2');" +
            "INSERT INTO mli_c VALUES (3, 'g3', 'p3', 'c3');");

        var full = Helpers.BackupPath("mli");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "mli_" + Guid.NewGuid().ToString("N")[..8];
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

            // Inheritance edges: gp<-p, p<-c.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_inherits";
                Assert.True(2L <= (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Grandparent SELECT returns all three rows.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM mli_gp";
                Assert.Equal(3L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            // Parent returns 2 (own + child).
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM mli_p";
                Assert.Equal(2L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            // Child column populated.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT c_col FROM mli_c WHERE id = 3";
                Assert.Equal("c3", (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
