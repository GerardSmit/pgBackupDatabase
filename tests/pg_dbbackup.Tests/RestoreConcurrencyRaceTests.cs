using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Two restores aimed at the same target_db must not both succeed and
/// leave a half-built or duplicated state. One should win; the other
/// must either also produce a consistent final DB or return an error
/// that names the conflict — never silently corrupt state.
/// </summary>
public sealed class RestoreConcurrencyRaceTests
{
    private readonly PgContainerFixture _pg;

    public RestoreConcurrencyRaceTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Two_Parallel_Restores_Same_Target_Land_Cleanly()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE race_t(id int PRIMARY KEY, v text);" +
            "INSERT INTO race_t " +
            "SELECT g, md5(g::text) FROM generate_series(1, 5000) g;");

        var full = Helpers.BackupPath("racerestore");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "race_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // Fire two restores from independent admin connections.
            // Either both succeed (idempotent rename-over), or one returns
            // an error that mentions the target.
            var t1 = Task.Run(() => TryRestoreAsync(target, full));
            var t2 = Task.Run(() => TryRestoreAsync(target, full));
            var results = await Task.WhenAll(t1, t2);

            var successes = results.Count(r => r.Error is null);
            Assert.True(successes >= 1,
                $"Expected at least one restore to succeed. Errors: " +
                string.Join(" | ", results.Select(r => r.Error?.Message)));

            // The losing call (if any) must mention the target or "exists"
            // / "locked" / "in use" — not a random low-level corruption.
            foreach (var r in results.Where(x => x.Error is not null))
            {
                var msg = r.Error!.Message.ToLowerInvariant();
                Assert.True(
                    msg.Contains(target.ToLowerInvariant()) ||
                    msg.Contains("exist") ||
                    msg.Contains("in use") ||
                    msg.Contains("lock") ||
                    msg.Contains("already") ||
                    msg.Contains("conflict") ||
                    msg.Contains("concurrent"),
                    $"Losing restore must surface an explanatory message: {r.Error.Message}");
            }

            // After both calls return, the final target DB must exist and
            // contain the full row set exactly once.
            Assert.True(await _pg.DbExistsAsync(target),
                "Target DB must exist after race");
            await using var r2 = await _pg.ConnectToAsync(target);
            await using var cmd = r2.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM race_t";
            var cnt = (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal(5000L, cnt);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task<RestoreResult> TryRestoreAsync(string target, string file)
    {
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { file });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 300;
            await cmd.ExecuteNonQueryAsync();
            return new RestoreResult(null);
        }
        catch (Exception ex)
        {
            return new RestoreResult(ex);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
        }
    }

    private sealed record RestoreResult(Exception? Error);
}
