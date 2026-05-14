using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER DEFAULT PRIVILEGES targeting sequences and functions, scoped to
/// a specific schema. The existing matrix test only covers tables in
/// schema public.
/// </summary>
public sealed class DefaultPrivilegesSchemaScopeTests
{
    private readonly PgContainerFixture _pg;

    public DefaultPrivilegesSchemaScopeTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Default_Privileges_For_Sequences_And_Functions_RoundTrip()
    {
        await using (var admin = await _pg.AdminAsync())
        {
            await admin.ExecAsync(
                "DO $$ BEGIN " +
                "IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname='dps_reader') " +
                "  THEN CREATE ROLE dps_reader; END IF; END $$;");
        }

        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE SCHEMA s_dps;" +
            "ALTER DEFAULT PRIVILEGES IN SCHEMA s_dps " +
            "  GRANT USAGE, SELECT ON SEQUENCES TO dps_reader;" +
            "ALTER DEFAULT PRIVILEGES IN SCHEMA s_dps " +
            "  GRANT EXECUTE ON FUNCTIONS TO dps_reader;");

        var full = Helpers.BackupPath("dps");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "dps_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT d.defaclobjtype, " +
                "       array_to_string(d.defaclacl, ',') " +
                "FROM pg_default_acl d " +
                "JOIN pg_namespace n ON n.oid = d.defaclnamespace " +
                "WHERE n.nspname = 's_dps' " +
                "ORDER BY d.defaclobjtype";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            var rows = new List<(char k, string acl)>();
            while (await rdr.ReadAsync(TestContext.Current.CancellationToken))
                rows.Add((rdr.GetChar(0), rdr.GetString(1)));

            Assert.Equal(2, rows.Count);
            // 'f' = functions, 'S' = sequences
            Assert.All(rows, row => Assert.Contains("dps_reader", row.acl));
            Assert.Contains(rows, row => row.k == 'f');
            Assert.Contains(rows, row => row.k == 'S');
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
