using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Functions with VARIADIC parameters and OUT parameters require
/// pg_get_function_arguments / pg_get_function_result emission, not a
/// naive column list. Round-trip must preserve call signature so
/// existing callers don't break.
/// </summary>
public sealed class VariadicFunctionTests
{
    private readonly PgContainerFixture _pg;

    public VariadicFunctionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Variadic_And_Out_Param_Functions_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE FUNCTION vf_sum_all(VARIADIC vals int[]) " +
            "RETURNS int LANGUAGE sql IMMUTABLE AS " +
            "  $$ SELECT coalesce(sum(v), 0)::int FROM unnest(vals) AS v $$;" +
            "CREATE FUNCTION vf_minmax(arr int[], OUT lo int, OUT hi int) " +
            "RETURNS record LANGUAGE sql IMMUTABLE AS " +
            "  $$ SELECT min(v)::int, max(v)::int FROM unnest(arr) AS v $$;");

        var full = Helpers.BackupPath("vf");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "vf_" + Guid.NewGuid().ToString("N")[..8];
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

            // Variadic invocation works.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT vf_sum_all(1, 2, 3, 4, 5)";
                Assert.Equal(15, (int)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // OUT parameters return record.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT lo, hi FROM vf_minmax(ARRAY[7, 3, 9, 1, 5])";
                await using var rdr = await c.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(
                    TestContext.Current.CancellationToken));
                Assert.Equal(1, rdr.GetInt32(0));
                Assert.Equal(9, rdr.GetInt32(1));
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
