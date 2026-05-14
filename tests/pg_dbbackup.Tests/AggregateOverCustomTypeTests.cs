using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// CREATE AGGREGATE backed by a user-defined state composite type, with
/// SFUNC and FINALFUNC. Tests that aggregates correctly survive restore
/// and remain usable.
/// </summary>
public sealed class AggregateOverCustomTypeTests
{
    private readonly PgContainerFixture _pg;

    public AggregateOverCustomTypeTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Aggregate_Over_Composite_State_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TYPE mean_state AS (sum bigint, cnt bigint);" +
            "CREATE FUNCTION mean_sfunc(s mean_state, v int) " +
            "  RETURNS mean_state LANGUAGE sql IMMUTABLE AS $$ " +
            "    SELECT ROW(COALESCE((s).sum,0) + v, COALESCE((s).cnt,0) + 1)::mean_state $$;" +
            "CREATE FUNCTION mean_ffunc(s mean_state) RETURNS numeric " +
            "  LANGUAGE sql IMMUTABLE AS $$ " +
            "    SELECT CASE WHEN (s).cnt = 0 THEN NULL " +
            "                ELSE (s).sum::numeric / (s).cnt END $$;" +
            "CREATE AGGREGATE my_mean(int) (" +
            "  SFUNC = mean_sfunc, STYPE = mean_state, FINALFUNC = mean_ffunc);" +
            "CREATE TABLE agg_t(id int PRIMARY KEY, v int);" +
            "INSERT INTO agg_t SELECT g, g FROM generate_series(1, 10) g;");

        var full = Helpers.BackupPath("agg_ct");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "agg_ct_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT my_mean(v) FROM agg_t";
            var v = (decimal)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal(5.5m, v);
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
