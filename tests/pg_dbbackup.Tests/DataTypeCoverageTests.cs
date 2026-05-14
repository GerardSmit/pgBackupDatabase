using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Round-trips a wide matrix of PostgreSQL data types through SIMPLE FULL
/// and through FULL-mode LOG replay. Verifies COPY binary encoding and
/// logical decoding both preserve representation for non-trivial types.
/// </summary>
public sealed class DataTypeCoverageTests
{
    private readonly PgContainerFixture _pg;

    public DataTypeCoverageTests(PgContainerFixture pg) => _pg = pg;

    private const string CreateTable =
        "CREATE TYPE color AS ENUM ('red','green','blue');" +
        "CREATE TYPE addr AS (street text, zip text);" +
        "CREATE TABLE matrix(" +
        "  id          int PRIMARY KEY," +
        "  i2          smallint NOT NULL," +
        "  big         bigint NOT NULL," +
        "  exact       numeric(38,12) NOT NULL," +
        "  approx      double precision NOT NULL," +
        "  flag        bit(4) NOT NULL," +
        "  bv          bit varying NOT NULL," +
        "  guid        uuid NOT NULL," +
        "  ip4         inet NOT NULL," +
        "  net6        cidr NOT NULL," +
        "  mac         macaddr NOT NULL," +
        "  iv          interval NOT NULL," +
        "  ts_tz       timestamptz NOT NULL," +
        "  ts_local    timestamp NOT NULL," +
        "  dt          date NOT NULL," +
        "  t           time NOT NULL," +
        "  tr          tstzrange NOT NULL," +
        "  arr_i       int[] NOT NULL," +
        "  arr_m       int[][] NOT NULL," +
        "  e           color NOT NULL," +
        "  composite   addr NOT NULL," +
        "  j           jsonb NOT NULL," +
        "  body        text NOT NULL" +
        ");";

    private const string InsertRow =
        "INSERT INTO matrix VALUES (" +
        "  1, 32100, 9223372036854775000, " +
        "  12345678901234567890.123456789012," +
        "  3.141592653589793," +
        "  B'1010'," +
        "  B'11001010'," +
        "  '11111111-2222-3333-4444-555555555555'," +
        "  '10.0.0.5/24'," +
        "  '2001:db8::/32'," +
        "  '08:00:2b:01:02:03'," +
        "  interval '1 year 2 months 3 days 4 hours 5 minutes 6.789 seconds'," +
        "  '2026-05-13 14:30:00+02'," +
        "  '2026-05-13 14:30:00'," +
        "  '2026-05-13'," +
        "  '14:30:45.123'," +
        "  tstzrange('2026-01-01 00:00+00','2026-02-01 00:00+00','[)')," +
        "  ARRAY[1,2,3,4,5]," +
        "  ARRAY[[1,2],[3,4]]," +
        "  'green'," +
        "  ROW('Elm Street','12345')," +
        "  '{\"k\":[1,2,3],\"n\":null}'::jsonb," +
        "  E'unicode \\u2603 snowman'" +
        ");";

    private const string CompareSql =
        "SELECT row_to_json(m)::text FROM matrix m WHERE id = 1";

    [Fact]
    public async Task DataTypeMatrix_Simple_Full_Restore_Preserves_Values()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(CreateTable);
        await src.ExecAsync(InsertRow);
        var srcRow = await ScalarStringAsync(src, CompareSql);

        var full = Helpers.BackupPath("dtm_simple");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = "dtm_simple_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full });
            await using var r = await _pg.ConnectToAsync(target);
            var restored = await ScalarStringAsync(r, CompareSql);
            Assert.Equal(srcRow, restored);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task DataTypeMatrix_FullMode_Log_Replay_Preserves_Values()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(CreateTable);

        var full = Helpers.BackupPath("dtm_full");
        await src.BackupFullAsync(full, compress: true, commandTimeoutSeconds: 180);

        // Insert AFTER full so only logical decoding can reconstruct it.
        await src.ExecAsync(InsertRow);
        var srcRow = await ScalarStringAsync(src, CompareSql);

        var log = Helpers.BackupPath("dtm_log");
        await src.BackupLogAsync(log, full, compress: true, commandTimeoutSeconds: 180);
        await src.CloseAsync();

        var target = "dtm_full_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, new[] { full, log });
            await using var r = await _pg.ConnectToAsync(target);
            var restored = await ScalarStringAsync(r, CompareSql);
            Assert.Equal(srcRow, restored);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    private async Task RestoreAsync(string target, string[] files)
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

    private static async Task<string> ScalarStringAsync(NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 60;
        return (string)(await cmd.ExecuteScalarAsync(
            TestContext.Current.CancellationToken))!;
    }
}
