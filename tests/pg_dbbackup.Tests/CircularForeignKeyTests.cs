using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Tables A and B reference each other via FK. Restore order must
/// either defer constraint validation (NOT VALID + ALTER VALIDATE) or
/// drop+recreate constraints around the data load. A naive emission
/// order would fail with a foreign-key violation.
/// </summary>
public sealed class CircularForeignKeyTests
{
    private readonly PgContainerFixture _pg;

    public CircularForeignKeyTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Circular_Fk_Pair_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE cfk_a(id int PRIMARY KEY, b_id int);" +
            "CREATE TABLE cfk_b(id int PRIMARY KEY, a_id int);" +
            "ALTER TABLE cfk_a ADD CONSTRAINT cfk_a_b_fk " +
            "  FOREIGN KEY (b_id) REFERENCES cfk_b(id) DEFERRABLE INITIALLY DEFERRED;" +
            "ALTER TABLE cfk_b ADD CONSTRAINT cfk_b_a_fk " +
            "  FOREIGN KEY (a_id) REFERENCES cfk_a(id) DEFERRABLE INITIALLY DEFERRED;" +
            "BEGIN;" +
            "  INSERT INTO cfk_a(id, b_id) VALUES (1, 10);" +
            "  INSERT INTO cfk_b(id, a_id) VALUES (10, 1);" +
            "COMMIT;");

        var full = Helpers.BackupPath("cfk");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "cfk_" + Guid.NewGuid().ToString("N")[..8];
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
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_constraint " +
                    "WHERE conname IN ('cfk_a_b_fk','cfk_b_a_fk') " +
                    "  AND contype = 'f' AND convalidated";
                Assert.Equal(2L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM cfk_a";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
