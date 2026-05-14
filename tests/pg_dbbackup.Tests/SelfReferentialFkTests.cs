using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// A self-referential foreign key (employee.manager_id -> employee.id)
/// makes the topological order of inserts non-trivial. The backup
/// must order the COPY/INSERT stream so parents precede children, or
/// defer constraint validation.
/// </summary>
public sealed class SelfReferentialFkTests
{
    private readonly PgContainerFixture _pg;

    public SelfReferentialFkTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Self_Referential_Fk_Tree_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE emp(" +
            "  id int PRIMARY KEY," +
            "  name text NOT NULL," +
            "  manager_id int REFERENCES emp(id)" +
            ");" +
            // Build a 3-level tree:
            //   1 CEO
            //   ├── 2 VP
            //   │   ├── 4 Lead
            //   │   └── 5 Senior
            //   └── 3 VP
            //       └── 6 Lead
            "INSERT INTO emp VALUES " +
            "  (1, 'CEO', NULL)," +
            "  (2, 'VP-A', 1)," +
            "  (3, 'VP-B', 1)," +
            "  (4, 'Lead-A1', 2)," +
            "  (5, 'Senior-A2', 2)," +
            "  (6, 'Lead-B1', 3);");

        var full = Helpers.BackupPath("selffk");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "sf_" + Guid.NewGuid().ToString("N")[..8];
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

            // All 6 rows present.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM emp";
                Assert.Equal(6L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // FK validated.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_constraint " +
                    "WHERE conrelid = 'emp'::regclass " +
                    "  AND contype = 'f' AND convalidated";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Recursive CTE finds full tree.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "WITH RECURSIVE tree AS (" +
                    "  SELECT id, name, manager_id, 0 AS depth FROM emp WHERE manager_id IS NULL" +
                    "  UNION ALL" +
                    "  SELECT e.id, e.name, e.manager_id, t.depth + 1 " +
                    "  FROM emp e JOIN tree t ON e.manager_id = t.id" +
                    ") SELECT count(*) FROM tree";
                Assert.Equal(6L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
