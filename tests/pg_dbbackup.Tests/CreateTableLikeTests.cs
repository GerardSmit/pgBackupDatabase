using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// CREATE TABLE ... (LIKE source INCLUDING ALL) clones columns,
/// constraints, indexes, and defaults from source at creation time;
/// it does NOT establish a runtime dependency. The restored DB should
/// have the destination's MATERIALIZED schema, not the LIKE clause.
/// </summary>
public sealed class CreateTableLikeTests
{
    private readonly PgContainerFixture _pg;

    public CreateTableLikeTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Table_Created_With_Like_RoundTrips_Materialized()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE like_src(" +
            "  id int PRIMARY KEY," +
            "  v text NOT NULL DEFAULT 'x'," +
            "  created_at timestamptz NOT NULL DEFAULT now()" +
            ");" +
            "CREATE TABLE like_dst (LIKE like_src INCLUDING ALL);" +
            "INSERT INTO like_dst(id, v) VALUES (1, 'a'), (2, 'b');");

        var full = Helpers.BackupPath("likedst");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "ld_" + Guid.NewGuid().ToString("N")[..8];
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

            // Both tables exist independently.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_class " +
                    "WHERE relname IN ('like_src','like_dst') AND relkind = 'r'";
                Assert.Equal(2L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // like_dst has the PK constraint cloned from like_src.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_constraint " +
                    "WHERE conrelid = 'like_dst'::regclass AND contype = 'p'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // DEFAULT on v survived. INSERT without explicit v uses 'x'.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "INSERT INTO like_dst(id) VALUES (3) RETURNING v";
                Assert.Equal("x", (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
