using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Pre-partition table inheritance (CREATE TABLE child INHERITS
/// (parent)) is distinct from declarative partitioning. The child
/// inherits columns and constraints but is a separate physical table.
/// DDL emission must preserve the INHERITS clause and parent column
/// types must remain inheritable.
/// </summary>
public sealed class InheritanceTableTests
{
    private readonly PgContainerFixture _pg;

    public InheritanceTableTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Inherits_Clause_RoundTrips_With_Rows_In_Both()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE inh_parent(id int PRIMARY KEY, kind text);" +
            "CREATE TABLE inh_child(extra text) INHERITS (inh_parent);" +
            "INSERT INTO inh_parent VALUES (1, 'p1'), (2, 'p2');" +
            "INSERT INTO inh_child VALUES (10, 'c1', 'extra1'), (11, 'c2', 'extra2');");

        var full = Helpers.BackupPath("inh");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "inh_" + Guid.NewGuid().ToString("N")[..8];
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

            // pg_inherits links child -> parent.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_inherits i " +
                    "JOIN pg_class child ON child.oid = i.inhrelid " +
                    "JOIN pg_class parent ON parent.oid = i.inhparent " +
                    "WHERE child.relname = 'inh_child' " +
                    "  AND parent.relname = 'inh_parent'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // SELECT from parent returns parent + child rows.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM inh_parent";
                Assert.Equal(4L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            // ONLY parent returns just 2.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM ONLY inh_parent";
                Assert.Equal(2L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            // Child has its own extra column populated.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT extra FROM inh_child WHERE id = 11";
                Assert.Equal("extra2", (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
