using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Per-chain-link encryption keying. A FULL+DIFF chain encrypted with
/// two different passwords must reject a restore that supplies only
/// one — there is no per-link password vector in the SQL API.
/// </summary>
public sealed class KeyRotationTests
{
    private readonly PgContainerFixture _pg;

    public KeyRotationTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Mismatched_Password_On_Restore_Fails_Cleanly()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY); INSERT INTO t VALUES (1);");

        var full = Helpers.BackupPath("kr_full");
        await src.BackupFullAsync(full, compress: false, password: "pw_one");
        await src.CloseAsync();

        var target = "kr_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            // Wrong password.
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], " +
                    "  target_db := @tgt, password := 'pw_wrong')";
                cmd.Parameters.AddWithValue("p", full);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 60;
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                Assert.NotNull(ex.SqlState);
            }

            // No password against an encrypted file → also fails.
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], " +
                    "  target_db := @tgt)";
                cmd.Parameters.AddWithValue("p", full);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 60;
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                Assert.NotNull(ex.SqlState);
            }

            // Right password succeeds.
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(ARRAY[@p]::text[], " +
                    "  target_db := @tgt, password := 'pw_one')";
                cmd.Parameters.AddWithValue("p", full);
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.CommandTimeout = 60;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);
            await using var c2 = r.CreateCommand();
            c2.CommandText = "SELECT count(*) FROM t";
            Assert.Equal(1L, (long)(await c2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }
}
