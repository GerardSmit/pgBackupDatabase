using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER TABLE ... ADD COLUMN ... GENERATED ALWAYS AS (...) STORED issued
/// during the LOG window. The DDL journal must capture the ADD COLUMN
/// statement, and post-restore SELECT must compute the generated
/// expression on existing rows (rewrite path).
/// </summary>
public sealed class GeneratedColumnAlterTests
{
    private readonly PgContainerFixture _pg;

    public GeneratedColumnAlterTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Add_Generated_Column_During_Log_Window_Replays()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE gca_t(id int PRIMARY KEY, base int NOT NULL);" +
            "INSERT INTO gca_t SELECT g, g FROM generate_series(1, 5) g;");

        var full = Helpers.BackupPath("gca_full");
        await src.BackupFullAsync(full);

        // Add a generated column; this rewrites the existing rows.
        await src.ExecAsync(
            "ALTER TABLE gca_t ADD COLUMN tripled int " +
            "  GENERATED ALWAYS AS (base * 3) STORED;");
        await src.ExecAsync("INSERT INTO gca_t(id, base) VALUES (6, 6);");

        var log = Helpers.BackupPath("gca_log");
        await src.BackupLogAsync(log, basePath: full);
        await src.CloseAsync();

        var target = "gca_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);
            await using var r = await _pg.ConnectToAsync(target);

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT id, tripled FROM gca_t ORDER BY id";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                var rows = new List<(int id, int t)>();
                while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                    rows.Add((rdr.GetInt32(0), rdr.GetInt32(1)));
                Assert.Equal(
                    new[] { (1,3), (2,6), (3,9), (4,12), (5,15), (6,18) },
                    rows);
            }

            // The new column is GENERATED — direct INSERT into it must fail.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO gca_t(id, base, tripled) VALUES (7, 7, 999)";
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => cmd.ExecuteNonQueryAsync(
                        TestContext.Current.CancellationToken));
                Assert.Equal("428C9", ex.SqlState);
            }
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
