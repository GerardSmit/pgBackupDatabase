using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// stop_at takes a timestamptz. The cutoff must respect microsecond
/// precision: txns committed at T-1us must be present, txns at T+1us
/// must be absent, even though both share the same millisecond.
/// </summary>
public sealed class PitrSubSecondPrecisionTests
{
    private readonly PgContainerFixture _pg;

    public PitrSubSecondPrecisionTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Stop_At_Resolves_To_Microsecond_Precision_On_Same_Millisecond()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE precise_t(id int PRIMARY KEY, v text);" +
            "INSERT INTO precise_t VALUES (1, 'base');");

        var full = Helpers.BackupPath("precise_full");
        await src.BackupFullAsync(full, compress: false);

        // Insert N rows, each in its OWN transaction so each commit
        // gets a distinct commit timestamp at microsecond resolution.
        // A procedure with COMMIT lets us loop without round-trips.
        await src.ExecAsync(
            "CREATE PROCEDURE precise_fill() LANGUAGE plpgsql AS $$ " +
            "BEGIN " +
            "  FOR i IN 2..201 LOOP " +
            "    INSERT INTO precise_t VALUES (i, 'r' || i); " +
            "    COMMIT; " +
            "  END LOOP; " +
            "END $$;");
        // Disable synchronous_commit so the COMMITs inside the
        // procedure can land in the same millisecond on slow CI disks.
        await src.ExecAsync("SET synchronous_commit = off;");
        await src.ExecAsync("CALL precise_fill();");
        await src.ExecAsync("CHECKPOINT;");

        // Collect (xid, commit_ts) of each row. precise_t row id = xid is
        // not guaranteed but pg_xact_commit_timestamp(xmin) returns the
        // true commit time of the row's transaction.
        var commits = new List<(int id, DateTime ts)>();
        await using (var cmd = src.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, pg_xact_commit_timestamp(xmin) " +
                "FROM precise_t WHERE id BETWEEN 2 AND 201 " +
                "ORDER BY id";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                commits.Add((rdr.GetInt32(0), rdr.GetDateTime(1)));
        }

        // Find two consecutive commits in the same millisecond. PG's
        // commit timestamp resolution is 1us; consecutive small INSERTs
        // typically land 10-50us apart, all sharing one ms.
        int? subMsIdx = null;
        for (var i = 1; i < commits.Count; i++)
        {
            var a = commits[i - 1].ts;
            var b = commits[i].ts;
            if (a.Date == b.Date &&
                a.Hour == b.Hour && a.Minute == b.Minute &&
                a.Second == b.Second &&
                a.Millisecond == b.Millisecond &&
                a.Ticks < b.Ticks)
            {
                subMsIdx = i; // commit at i sits sub-ms after i-1
                break;
            }
        }
        Assert.NotNull(subMsIdx);

        var earlier = commits[subMsIdx!.Value - 1];
        var later = commits[subMsIdx.Value];

        var log = Helpers.BackupPath("precise_log");
        await src.BackupLogAsync(log, full, compress: false);
        await src.CloseAsync();

        // Cutoff = earlier.ts exactly. Row `earlier.id` is in; `later.id`
        // is out. Both share the same millisecond.
        var target = "precise_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, earlier.ts, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT array_agg(id ORDER BY id) FROM precise_t " +
                "WHERE id IN (@a, @b)";
            cmd.Parameters.AddWithValue("a", earlier.id);
            cmd.Parameters.AddWithValue("b", later.id);
            var present = (int[])(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;

            // earlier.id must be visible.
            Assert.Contains(earlier.id, present);
            // later.id (sub-ms after the cutoff) must NOT be visible.
            Assert.DoesNotContain(later.id, present);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, DateTime stopAt, string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@files::text[], " +
            "target_db := @target, stop_at := @cutoff)";
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("target", target);
        cmd.Parameters.AddWithValue("cutoff", stopAt);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
