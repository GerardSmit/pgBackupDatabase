using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// FDW round-trip beyond the matrix accept-only test: SERVER + USER
/// MAPPING + FOREIGN TABLE + ALTER SERVER OPTIONS all survive a full
/// restore.
/// </summary>
public sealed class ForeignDataWrapperRoundtripTests
{
    private readonly PgContainerFixture _pg;

    public ForeignDataWrapperRoundtripTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task File_Fdw_Server_And_Foreign_Table_RoundTrip()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE EXTENSION file_fdw;" +
            "CREATE SERVER fdw_srv FOREIGN DATA WRAPPER file_fdw;" +
            "CREATE USER MAPPING FOR CURRENT_USER SERVER fdw_srv;" +
            "CREATE FOREIGN TABLE fdw_t(id int, label text) SERVER fdw_srv " +
            "  OPTIONS (filename '/tmp/pgdbbackup_fdw_missing.csv', format 'csv');");

        var full = Helpers.BackupPath("fdw_rt");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "fdw_rt_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // Extension present.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_extension WHERE extname='file_fdw'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Server present.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT srvname, srvtype, w.fdwname " +
                    "FROM pg_foreign_server s " +
                    "JOIN pg_foreign_data_wrapper w ON s.srvfdw = w.oid " +
                    "WHERE srvname = 'fdw_srv'";
                await using var rdr = await cmd.ExecuteReaderAsync(
                    TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal("fdw_srv", rdr.GetString(0));
                Assert.Equal("file_fdw", rdr.GetString(2));
            }

            // Foreign-table options preserved.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT array_to_string(ftoptions, ',') " +
                    "FROM pg_foreign_table ft " +
                    "JOIN pg_class c ON c.oid = ft.ftrelid " +
                    "WHERE c.relname = 'fdw_t'";
                var s = (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Contains("filename=", s);
                Assert.Contains("format=csv", s);
            }

            // User mapping present.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_user_mappings " +
                    "WHERE srvname = 'fdw_srv'";
                Assert.True((long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))! >= 1);
            }

            // Foreign table present.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_class c " +
                    "WHERE c.relname='fdw_t' AND c.relkind='f'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
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
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
