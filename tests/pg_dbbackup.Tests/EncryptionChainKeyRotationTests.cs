using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Encryption key per chain link. Real-world rotation pattern: a
/// FULL is taken with one key, the operator rotates the password, and
/// the subsequent DIFF/LOG backups use the new key. Restore must
/// reject mixed-key chains unambiguously when a single password is
/// supplied — there is no per-link password vector in the API.
/// </summary>
public sealed class EncryptionChainKeyRotationTests
{
    private readonly PgContainerFixture _pg;

    public EncryptionChainKeyRotationTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Full_And_Diff_With_Different_Passwords_Restore_Rejects_Either_Single_Password()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE rot_t(id int PRIMARY KEY, v text);" +
            "INSERT INTO rot_t VALUES (1,'a');");

        var full = Helpers.BackupPath("rotfull");
        await src.BackupFullAsync(full, compress: false, password: "pw_old");

        await src.ExecAsync("INSERT INTO rot_t VALUES (2,'b');");
        var diff = Helpers.BackupPath("rotdiff");

        Exception? diffWithNewPwErr = null;
        try
        {
            // DIFF uses pw_new (rotation).
            await using var cmd = src.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'differential', " +
                "compress := false, password := 'pw_new', " +
                "base_filepath := @base)";
            cmd.Parameters.AddWithValue("db", src.Database!);
            cmd.Parameters.AddWithValue("path", diff);
            cmd.Parameters.AddWithValue("base", full);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        catch (PostgresException ex) { diffWithNewPwErr = ex; }
        await src.CloseAsync();

        // Two possible behaviors for the producer:
        // A) Producer rejects rotation mid-chain — backup fails. Then the
        //    chain never exists with mixed keys; this satisfies the
        //    safety property.
        if (diffWithNewPwErr is PostgresException pex)
        {
            var msg = pex.MessageText.ToLowerInvariant();
            Assert.True(
                msg.Contains("password") || msg.Contains("key") ||
                msg.Contains("encrypt") || msg.Contains("base") ||
                msg.Contains("chain"),
                $"Expected mixed-key DIFF refusal to mention encryption: {pex.MessageText}");
            return;
        }

        // B) Producer accepted the rotation. Restore with EITHER single
        //    password must fail cleanly — it can't decrypt both links.
        var target = "rot_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            foreach (var pw in new[] { "pw_old", "pw_new" })
            {
                await using var admin = await _pg.AdminAsync();
                await using var cmd = admin.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore(@files::text[], " +
                    "  target_db := @tgt, password := @pw)";
                cmd.Parameters.AddWithValue("files", new[] { full, diff });
                cmd.Parameters.AddWithValue("tgt", target);
                cmd.Parameters.AddWithValue("pw", pw);
                cmd.CommandTimeout = 60;
                var rex = await Assert.ThrowsAsync<PostgresException>(
                    () => cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                Assert.NotNull(rex.SqlState);
            }
        }
        finally { try { await _pg.DropDbAsync(target); } catch { } }
    }
}
