using System.Text;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class LogicalIsolationTests
{
    private readonly PgContainerFixture _pg;

    public LogicalIsolationTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Full_Backup_Does_Not_Contain_Other_Database_Data()
    {
        await using var target = await _pg.CreateFreshDbWithExtensionAsync();
        await target.SetModeFullAsync();
        await target.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, payload text);" +
            "INSERT INTO t VALUES (1, 'target-visible');");

        var otherDb = "other_" + Guid.NewGuid().ToString("N")[..8];
        var secret = "other_db_secret_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText = $"CREATE DATABASE \"{otherDb}\"";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using (var other = await _pg.ConnectToAsync(otherDb))
            {
                await other.ExecAsync(
                    "CREATE TABLE leakcheck(id int PRIMARY KEY, payload text);" +
                    $"INSERT INTO leakcheck VALUES (1, '{secret}');");
            }

            var path = Helpers.BackupPath("isolation");
            await target.BackupFullAsync(path);

            var bytes = await _pg.ReadContainerFileAsync(path);
            var text = Encoding.UTF8.GetString(bytes);

            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("global/", text);
            Assert.DoesNotContain("pg_xact/", text);
            Assert.DoesNotContain("pg_control", text);
        }
        finally
        {
            try { await _pg.DropDbAsync(otherDb); } catch { }
        }
    }

    [Fact]
    public async Task Log_Backup_Does_Not_Contain_Other_Database_Data()
    {
        await using var target = await _pg.CreateFreshDbWithExtensionAsync();
        await target.SetModeFullAsync();
        await target.ExecAsync(
            "CREATE TABLE t(id int PRIMARY KEY, payload text);" +
            "INSERT INTO t VALUES (1, 'target-before');");

        var fullPath = Helpers.BackupPath("isolation");
        await target.BackupFullAsync(fullPath);

        var otherDb = "other_" + Guid.NewGuid().ToString("N")[..8];
        var secret = "other_db_log_secret_" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var admin = await _pg.AdminAsync())
            await using (var cmd = admin.CreateCommand())
            {
                cmd.CommandText = $"CREATE DATABASE \"{otherDb}\"";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using (var other = await _pg.ConnectToAsync(otherDb))
            {
                await other.ExecAsync(
                    "CREATE TABLE leakcheck(id int PRIMARY KEY, payload text);" +
                    $"INSERT INTO leakcheck VALUES (1, '{secret}');");
            }

            await target.ExecAsync("INSERT INTO t VALUES (2, 'target-after');");

            var logPath = Helpers.BackupPath("isolation");
            await target.BackupLogAsync(logPath, fullPath);

            var bytes = await _pg.ReadContainerFileAsync(logPath);
            var text = Encoding.UTF8.GetString(bytes);

            Assert.DoesNotContain(secret, text);
            Assert.Contains("target-after", text);
        }
        finally
        {
            try { await _pg.DropDbAsync(otherDb); } catch { }
        }
    }
}
