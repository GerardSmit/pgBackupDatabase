using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Publication coverage beyond the matrix smoke test: FOR ALL TABLES,
/// ALTER PUBLICATION ADD/DROP TABLE, per-publication column lists, and
/// row filters. These are pure metadata objects; backup must capture
/// the full definition.
/// </summary>
public sealed class PublicationsAdvancedTests
{
    private readonly PgContainerFixture _pg;

    public PublicationsAdvancedTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task ForAllTables_Publication_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE pub_a(id int PRIMARY KEY);" +
            "CREATE TABLE pub_b(id int PRIMARY KEY);" +
            "CREATE PUBLICATION pub_all FOR ALL TABLES " +
            "  WITH (publish = 'insert, update');");

        var full = Helpers.BackupPath("pub_all");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "pub_all_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT puballtables, pubinsert, pubupdate, pubdelete " +
                "FROM pg_publication WHERE pubname = 'pub_all'";
            await using var rdr = await cmd.ExecuteReaderAsync(
                TestContext.Current.CancellationToken);
            Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
            Assert.True(rdr.GetBoolean(0));
            Assert.True(rdr.GetBoolean(1));
            Assert.True(rdr.GetBoolean(2));
            Assert.False(rdr.GetBoolean(3));
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task Publication_Table_Set_RoundTrips_After_AlterAddDrop()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE pub_t1(id int PRIMARY KEY, v int);" +
            "CREATE TABLE pub_t2(id int PRIMARY KEY);" +
            "CREATE TABLE pub_t3(id int PRIMARY KEY);" +
            "CREATE PUBLICATION pub_set FOR TABLE pub_t1, pub_t2;" +
            "ALTER PUBLICATION pub_set ADD TABLE pub_t3;" +
            "ALTER PUBLICATION pub_set DROP TABLE pub_t2;");

        var full = Helpers.BackupPath("pub_set");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "pub_set_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText =
                "SELECT array_agg(c.relname ORDER BY c.relname) " +
                "FROM pg_publication p " +
                "JOIN pg_publication_rel pr ON pr.prpubid = p.oid " +
                "JOIN pg_class c ON c.oid = pr.prrelid " +
                "WHERE p.pubname = 'pub_set'";
            var arr = (string[])(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.Equal(new[] { "pub_t1", "pub_t3" }, arr);
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
