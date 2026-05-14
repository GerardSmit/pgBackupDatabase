using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// numeric, double precision and real all support NaN, Infinity, and
/// -Infinity. Serialization must round-trip these specials; without
/// dedicated handling many text serializers turn them into NULL or 0.
/// </summary>
public sealed class NumericNanAndInfinityTests
{
    private readonly PgContainerFixture _pg;

    public NumericNanAndInfinityTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Float_And_Numeric_Specials_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE nani(" +
            "  id int PRIMARY KEY," +
            "  n numeric NOT NULL," +
            "  d double precision NOT NULL," +
            "  r real NOT NULL" +
            ");" +
            "INSERT INTO nani VALUES " +
            "  (1, 'NaN'::numeric, 'NaN'::double precision, 'NaN'::real)," +
            "  (2, 'Infinity'::numeric, 'Infinity'::double precision, 'Infinity'::real)," +
            "  (3, '-Infinity'::numeric, '-Infinity'::double precision, '-Infinity'::real)," +
            "  (4, 3.14159265358979323846::numeric(30,20), " +
            "      3.141592653589793::double precision, 3.14159::real);");

        var full = Helpers.BackupPath("nani");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "nani_" + Guid.NewGuid().ToString("N")[..8];
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

            // All specials preserved.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM nani " +
                    "WHERE n = 'NaN'::numeric " +
                    "  AND d = 'NaN'::double precision " +
                    "  AND r = 'NaN'::real";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM nani " +
                    "WHERE n = 'Infinity'::numeric " +
                    "  AND d = 'Infinity'::double precision";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM nani " +
                    "WHERE n = '-Infinity'::numeric " +
                    "  AND d = '-Infinity'::double precision";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // High-precision numeric preserves digits past double's range.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT n::text FROM nani WHERE id = 4";
                var s = (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.StartsWith("3.14159265358979323846", s);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
