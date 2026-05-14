using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// ALTER TABLE child INHERIT parent issued after both tables exist.
/// Different code path from CREATE TABLE ... INHERITS — backed by
/// ddl_gen_inheritance reading pg_inherits.
/// </summary>
public sealed class AlterTableInheritPostCreateTests
{
    private readonly PgContainerFixture _pg;

    public AlterTableInheritPostCreateTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Inherit_Attached_After_Create_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE parent_t(id int PRIMARY KEY, kind text);" +
            "CREATE TABLE child_t(id int PRIMARY KEY, kind text, extra text);" +
            "ALTER TABLE child_t INHERIT parent_t;" +
            "INSERT INTO parent_t VALUES (1,'p');" +
            "INSERT INTO child_t VALUES (2,'c','x');");

        var full = Helpers.BackupPath("inh_post");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "inh_post_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_inherits " +
                    "WHERE inhparent = 'parent_t'::regclass " +
                    "  AND inhrelid = 'child_t'::regclass";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // SELECT parent including child via inheritance.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM parent_t";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(
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
