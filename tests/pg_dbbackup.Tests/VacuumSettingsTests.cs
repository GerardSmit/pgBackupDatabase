using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Per-table storage parameters: fillfactor and autovacuum overrides.
/// Operators tune these per-table; they must survive a restore.
/// </summary>
public sealed class VacuumSettingsTests
{
    private readonly PgContainerFixture _pg;

    public VacuumSettingsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Table_Reloptions_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE vac_t(id int PRIMARY KEY, v text) " +
            "WITH (fillfactor = 70, " +
            "      autovacuum_vacuum_scale_factor = 0.05, " +
            "      autovacuum_analyze_scale_factor = 0.02);");

        var full = Helpers.BackupPath("vac");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "vac_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT array_to_string(reloptions, ',') " +
                "FROM pg_class WHERE oid = 'vac_t'::regclass";
            var v = await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken);
            Assert.NotNull(v);
            Assert.NotEqual(DBNull.Value, v);
            var s = (string)v!;
            Assert.Contains("fillfactor=70", s);
            Assert.Contains("autovacuum_vacuum_scale_factor=0.05", s);
            Assert.Contains("autovacuum_analyze_scale_factor=0.02", s);
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
