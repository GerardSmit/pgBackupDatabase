using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class ReplicaIdentityAndTruncateTests
{
    private readonly PgContainerFixture _pg;

    public ReplicaIdentityAndTruncateTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Generated_Stored_Column_Update_Replays_Through_Log()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE gen_t(" +
            "  id int PRIMARY KEY," +
            "  qty int NOT NULL," +
            "  doubled int GENERATED ALWAYS AS (qty * 2) STORED);" +
            "INSERT INTO gen_t(id, qty) VALUES (1, 5);");

        var full = Helpers.BackupPath("gen_full");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync("UPDATE gen_t SET qty = 11 WHERE id = 1;");

        var log = Helpers.BackupPath("gen_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "gen_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT qty, doubled FROM gen_t WHERE id = 1";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.Equal(11, rdr.GetInt32(0));
            Assert.Equal(22, rdr.GetInt32(1));
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Replica_Identity_Full_NoPk_Update_Delete_Replays()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE rif_t(a int NOT NULL, b text NOT NULL);" +
            "ALTER TABLE rif_t REPLICA IDENTITY FULL;" +
            "INSERT INTO rif_t VALUES (1, 'one'), (2, 'two'), (3, 'three');");

        var full = Helpers.BackupPath("rif_full");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync(
            "UPDATE rif_t SET b = 'TWO' WHERE a = 2;" +
            "DELETE FROM rif_t WHERE a = 3;" +
            "INSERT INTO rif_t VALUES (4, 'four');");

        var log = Helpers.BackupPath("rif_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "rif_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT a, b FROM rif_t ORDER BY a";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var rows = new List<(int, string)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add((rdr.GetInt32(0), rdr.GetString(1)));
            Assert.Equal(new[]
            {
                (1, "one"),
                (2, "TWO"),
                (4, "four"),
            }, rows);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task TruncateCascade_Replays_Through_Journal()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE tc_p(id int PRIMARY KEY);" +
            "CREATE TABLE tc_c(id int PRIMARY KEY, pid int " +
            "  REFERENCES tc_p(id) ON DELETE CASCADE);" +
            "INSERT INTO tc_p VALUES (1), (2);" +
            "INSERT INTO tc_c VALUES (10, 1), (20, 2);");

        var full = Helpers.BackupPath("tc_full");
        await src.BackupFullAsync(full, compress: true);

        await src.ExecAsync("TRUNCATE tc_p CASCADE;");

        var log = Helpers.BackupPath("tc_log");
        await src.BackupLogAsync(log, full, compress: true);
        await src.CloseAsync();

        var target = "tc_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd1 = r.CreateCommand();
            cmd1.CommandText = "SELECT count(*) FROM tc_p";
            Assert.Equal(0L, (long)(await cmd1.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
            await using var cmd2 = r.CreateCommand();
            cmd2.CommandText = "SELECT count(*) FROM tc_c";
            Assert.Equal(0L, (long)(await cmd2.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!);
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
