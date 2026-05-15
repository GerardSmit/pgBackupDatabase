using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Composite (multi-column) foreign key referencing a composite primary
/// key. Constraint emission must preserve column ordering and the
/// ON UPDATE / ON DELETE actions.
/// </summary>
public sealed class MultiColumnFkTests
{
    private readonly PgContainerFixture _pg;

    public MultiColumnFkTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Composite_Fk_RoundTrips_With_Column_Order_Preserved()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE mfk_parent(" +
            "  tenant_id int," +
            "  item_id int," +
            "  PRIMARY KEY (tenant_id, item_id));" +
            "CREATE TABLE mfk_child(" +
            "  cid int PRIMARY KEY," +
            "  tenant_id int," +
            "  item_id int," +
            "  FOREIGN KEY (tenant_id, item_id) " +
            "    REFERENCES mfk_parent(tenant_id, item_id) " +
            "    ON UPDATE CASCADE ON DELETE RESTRICT);" +
            "INSERT INTO mfk_parent VALUES (1, 100), (1, 200), (2, 100);" +
            "INSERT INTO mfk_child VALUES " +
            "  (10, 1, 100), (11, 1, 200), (12, 2, 100);");

        var full = Helpers.BackupPath("mfk");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "mfk_" + Guid.NewGuid().ToString("N")[..8];
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

            // FK column order preserved.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT a.attname FROM pg_constraint con " +
                    "JOIN unnest(con.conkey) WITH ORDINALITY AS k(attnum, ord) ON true " +
                    "JOIN pg_attribute a ON a.attrelid = con.conrelid AND a.attnum = k.attnum " +
                    "WHERE con.conrelid = 'mfk_child'::regclass " +
                    "  AND con.contype = 'f' " +
                    "ORDER BY k.ord";
                var cols = new List<string>();
                await using var rdr = await c.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                    cols.Add(rdr.GetString(0));
                Assert.Equal(new[] { "tenant_id", "item_id" }, cols.ToArray());
            }

            // ON DELETE RESTRICT prevents parent deletion.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "DELETE FROM mfk_parent WHERE tenant_id = 1 AND item_id = 100";
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => c.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                // PG 17 reports foreign_key_violation (23503); PG 18 reports
                // the more specific restrict_violation (23001).
                Assert.Contains(ex.SqlState, new[] { "23503", "23001" });
            }

            // ON UPDATE CASCADE propagates.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "UPDATE mfk_parent SET item_id = 999 " +
                    "WHERE tenant_id = 2 AND item_id = 100";
                await c.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT item_id FROM mfk_child WHERE cid = 12";
                Assert.Equal(999, (int)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
