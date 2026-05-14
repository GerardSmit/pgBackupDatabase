using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// INSERT ... ON CONFLICT (..) DO UPDATE writes through to existing
/// rows. The logical decoding stream must surface these as UPDATEs (or
/// INSERTs for the new branch). Replay through DIFF/LOG must reproduce
/// the final state regardless of which branch each row took.
/// </summary>
public sealed class UpsertOnConflictTests
{
    private readonly PgContainerFixture _pg;

    public UpsertOnConflictTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Upsert_Replay_Matches_Source()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE uo_t(id int PRIMARY KEY, ver int NOT NULL, v text);" +
            "INSERT INTO uo_t VALUES (1, 1, 'one'), (2, 1, 'two');");

        var full = Helpers.BackupPath("uo");
        await src.BackupFullAsync(full, compress: false);

        // Mixed inserts and conflict-update.
        await src.ExecAsync(
            "INSERT INTO uo_t VALUES (1, 2, 'one-v2') " +
            "  ON CONFLICT (id) DO UPDATE SET ver = EXCLUDED.ver, v = EXCLUDED.v;" +
            "INSERT INTO uo_t VALUES (3, 1, 'three') " +
            "  ON CONFLICT (id) DO NOTHING;" +
            "INSERT INTO uo_t VALUES (2, 2, 'two-v2') " +
            "  ON CONFLICT (id) DO UPDATE SET ver = uo_t.ver + 1, v = EXCLUDED.v;");

        var log = Helpers.BackupPath("uo_log");
        await src.BackupLogAsync(log, full, compress: false);

        // Snapshot expected state.
        var rows = new List<(int id, int ver, string v)>();
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText = "SELECT id, ver, v FROM uo_t ORDER BY id";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add((rdr.GetInt32(0), rdr.GetInt32(1), rdr.GetString(2)));
        }
        await src.CloseAsync();

        var target = "uo_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full, log });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);
            await using var c = r.CreateCommand();
            c.CommandText = "SELECT id, ver, v FROM uo_t ORDER BY id";
            await using var rdr = await c.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var got = new List<(int id, int ver, string v)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                got.Add((rdr.GetInt32(0), rdr.GetInt32(1), rdr.GetString(2)));
            Assert.Equal(rows, got);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
