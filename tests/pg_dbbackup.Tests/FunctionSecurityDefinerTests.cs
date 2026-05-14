using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Function attributes the FULL backup must preserve: SECURITY DEFINER,
/// LEAKPROOF, STABLE / IMMUTABLE / VOLATILE volatility, parallel safety.
/// </summary>
public sealed class FunctionSecurityDefinerTests
{
    private readonly PgContainerFixture _pg;

    public FunctionSecurityDefinerTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Function_Attributes_Roundtrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE FUNCTION fn_sd(x int) RETURNS int " +
            "  LANGUAGE sql " +
            "  IMMUTABLE LEAKPROOF PARALLEL SAFE " +
            "  SECURITY DEFINER " +
            "  AS $$ SELECT x + 1 $$;" +
            "ALTER FUNCTION fn_sd(int) SET search_path = pg_catalog, public;");

        var full = Helpers.BackupPath("fn_sd");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "fn_sd_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT prosecdef, proleakproof, provolatile, proparallel, proconfig " +
                "FROM pg_proc WHERE proname = 'fn_sd'";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(rdr.GetBoolean(0));        // SECURITY DEFINER
            Assert.True(rdr.GetBoolean(1));        // LEAKPROOF
            Assert.Equal('i', rdr.GetChar(2));     // IMMUTABLE
            Assert.Equal('s', rdr.GetChar(3));     // PARALLEL SAFE
            Assert.False(rdr.IsDBNull(4));         // proconfig holds search_path
            var cfg = (string[])rdr.GetValue(4);
            Assert.Contains(cfg, s => s.StartsWith("search_path=",
                StringComparison.Ordinal));
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
