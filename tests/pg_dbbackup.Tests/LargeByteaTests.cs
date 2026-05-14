using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Multi-MB bytea single-row TOAST roundtrip. Validates large binary
/// values survive backup compression + restore intact.
/// </summary>
public sealed class LargeByteaTests
{
    private readonly PgContainerFixture _pg;

    public LargeByteaTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Large_Bytea_RoundTrips_With_Identical_Hash()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE lb_t(id int PRIMARY KEY, blob bytea NOT NULL);");

        // ~4 MB bytea: random-looking content via repeating md5 chunks.
        await src.ExecAsync(
            "INSERT INTO lb_t " +
            "SELECT 1, " +
            "       string_agg(decode(md5(g::text), 'hex'), ''::bytea) " +
            "FROM generate_series(1, 262144) g;",
            timeoutSeconds: 180);

        await using (var hashCmd = src.CreateCommand())
        {
            hashCmd.CommandText = "SELECT md5(blob) FROM lb_t WHERE id = 1";
            var srcHash = (string)(await hashCmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;

            var full = Helpers.BackupPath("lb");
            await src.BackupFullAsync(full, compress: true,
                commandTimeoutSeconds: 600);
            await src.CloseAsync();

            var target = "lb_" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                await RestoreAsync(target, full);
                await using var r = await _pg.ConnectToAsync(target);

                await using var cmd = r.CreateCommand();
                cmd.CommandText =
                    "SELECT octet_length(blob), md5(blob) FROM lb_t WHERE id = 1";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(262144 * 16, rdr.GetInt32(0));
                Assert.Equal(srcHash, rdr.GetString(1));
            }
            finally
            {
                try { await _pg.DropDbAsync(target); } catch { }
            }
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
        cmd.CommandTimeout = 600;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
