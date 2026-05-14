using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Multi-MB jsonb document survives backup and restore intact. Exercises
/// TOAST compression + storage on a large variable-length value.
/// </summary>
public sealed class LargeJsonbTests
{
    private readonly PgContainerFixture _pg;

    public LargeJsonbTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Multi_Megabyte_Jsonb_RoundTrips_With_Identical_Hash()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE lj_t(id int PRIMARY KEY, payload jsonb NOT NULL);");

        // ~2 MB jsonb: a single array with many distinct strings.
        await src.ExecAsync(
            "INSERT INTO lj_t " +
            "SELECT 1, " +
            "       jsonb_build_object('items', " +
            "         (SELECT jsonb_agg(jsonb_build_object(" +
            "           'k', g, " +
            "           'v', md5(g::text) || md5((g*7)::text))) " +
            "          FROM generate_series(1, 25000) g))",
            timeoutSeconds: 120);

        var src_hash = await ScalarStringAsync(src,
            "SELECT encode(digest(payload::text::bytea, 'sha256'), 'hex') " +
            "FROM lj_t WHERE id = 1") ??
            await ScalarStringAsync(src,
                "SELECT md5(payload::text) FROM lj_t WHERE id = 1");

        var full = Helpers.BackupPath("ljb");
        await src.BackupFullAsync(full, compress: true,
            commandTimeoutSeconds: 300);
        await src.CloseAsync();

        var target = "ljb_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            var dst_hash = await ScalarStringAsync(r,
                "SELECT encode(digest(payload::text::bytea, 'sha256'), 'hex') " +
                "FROM lj_t WHERE id = 1") ??
                await ScalarStringAsync(r,
                    "SELECT md5(payload::text) FROM lj_t WHERE id = 1");
            Assert.Equal(src_hash, dst_hash);

            // Sanity: the array has 25000 entries.
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT jsonb_array_length(payload->'items') FROM lj_t WHERE id = 1";
            Assert.Equal(25000, (int)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private static async Task<string?> ScalarStringAsync(
        NpgsqlConnection conn, string sql)
    {
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            var v = await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken);
            return v as string;
        }
        catch (PostgresException)
        {
            return null;
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
