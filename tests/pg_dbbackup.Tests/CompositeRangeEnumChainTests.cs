using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Custom-type round-trips: composite type used as a column, multirange,
/// and enum extension via ALTER TYPE ... ADD VALUE during the LOG window.
/// </summary>
public sealed class CompositeRangeEnumChainTests
{
    private readonly PgContainerFixture _pg;

    public CompositeRangeEnumChainTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Composite_Type_Column_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TYPE addr AS (street text, city text, zip text);" +
            "CREATE TABLE composites(id int PRIMARY KEY, home addr NOT NULL);" +
            "INSERT INTO composites VALUES " +
            "  (1, ROW('1 Main','NYC','10001')::addr), " +
            "  (2, ROW('2 Oak','SF','94102')::addr);");

        var full = Helpers.BackupPath("composite_rt");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "composite_rt_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT (home).city FROM composites WHERE id = 2";
            Assert.Equal("SF", (string)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Multirange_Column_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE mr_t(id int PRIMARY KEY, spans int4multirange NOT NULL);" +
            "INSERT INTO mr_t VALUES " +
            "  (1, '{[1,5),[10,15)}'::int4multirange);");

        var full = Helpers.BackupPath("multirange_rt");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "multirange_rt_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT spans::text FROM mr_t WHERE id = 1";
            var s = (string)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Contains("[1,5)", s);
            Assert.Contains("[10,15)", s);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Enum_AddValue_During_Log_Window_Replays()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TYPE color AS ENUM ('red','green');" +
            "CREATE TABLE color_t(id int PRIMARY KEY, c color NOT NULL);" +
            "INSERT INTO color_t VALUES (1,'red'),(2,'green');");

        var full = Helpers.BackupPath("enum_chain_full");
        await src.BackupFullAsync(full);

        // ALTER TYPE ... ADD VALUE cannot be used in the same tx; issue it
        // as its own statement so the new label is committed before the
        // INSERT references it.
        await src.ExecAsync("ALTER TYPE color ADD VALUE 'blue'");
        await src.ExecAsync("INSERT INTO color_t VALUES (3,'blue')");

        var log1 = Helpers.BackupPath("enum_chain_log1");
        await src.BackupLogAsync(log1, basePath: full);
        await src.CloseAsync();

        var target = "enum_chain_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log1);
            await using var r = await _pg.ConnectToAsync(target);
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT array_agg(enumlabel ORDER BY enumsortorder) " +
                    "FROM pg_enum e " +
                    "JOIN pg_type t ON t.oid = e.enumtypid " +
                    "WHERE t.typname = 'color'";
                var arr = (string[])(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { "red", "green", "blue" }, arr);
            }
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT c::text FROM color_t WHERE id = 3";
                Assert.Equal("blue", (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, params string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("tgt", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
