using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Standalone-sequence and OWNED BY round-trips. Identity columns are
/// covered separately. These cover bare SEQUENCE objects with manual
/// advance and OWNED BY column linkage.
/// </summary>
public sealed class SequenceValueAdvancedTests
{
    private readonly PgContainerFixture _pg;

    public SequenceValueAdvancedTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Standalone_Sequence_LastValue_Preserved()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE SEQUENCE my_seq " +
            "  INCREMENT 7 START 100 MINVALUE 100 MAXVALUE 1000 CACHE 1;" +
            // Advance to 142.
            "SELECT nextval('my_seq');" +
            "SELECT nextval('my_seq');" +
            "SELECT nextval('my_seq');" +
            "SELECT nextval('my_seq');" +
            "SELECT nextval('my_seq');" +
            "SELECT nextval('my_seq');" +
            "SELECT nextval('my_seq');");

        var full = Helpers.BackupPath("seq_val");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "seq_val_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);
            await using var cmd = r.CreateCommand();
            cmd.CommandText = "SELECT nextval('my_seq')";
            var next = (long)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            // Last value at backup time was 142 (100 + 6*7); nextval ⇒ 149.
            Assert.Equal(149L, next);
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task OwnedBy_Sequence_Survives_Restore_And_Drops_With_Table()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE SEQUENCE counter_seq;" +
            "CREATE TABLE counters(" +
            "  id int PRIMARY KEY DEFAULT nextval('counter_seq'), " +
            "  v text NOT NULL);" +
            "ALTER SEQUENCE counter_seq OWNED BY counters.id;" +
            "INSERT INTO counters(v) VALUES ('a'),('b'),('c');");

        var full = Helpers.BackupPath("seq_owned");
        await src.BackupFullAsync(full, compress: true);
        await src.CloseAsync();

        var target = "seq_owned_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);
            await using var r = await _pg.ConnectToAsync(target);

            // OWNED BY linkage survives.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT pg_describe_object(refclassid, refobjid, refobjsubid) " +
                    "FROM pg_depend " +
                    "WHERE deptype = 'a' " +
                    "  AND classid = 'pg_class'::regclass " +
                    "  AND objid = 'counter_seq'::regclass";
                var s = (string)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Contains("counters", s);
                Assert.Contains("id", s);
            }

            // Sequence value preserved → next insert must use id > 3.
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO counters(v) VALUES ('post') RETURNING id";
                var id = (int)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.True(id > 3, $"Expected id > 3, got {id}");
            }

            // Drop the table — sequence must drop too because it is OWNED.
            await r.ExecAsync("DROP TABLE counters");
            await using (var cmd = r.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM pg_class WHERE relname = 'counter_seq'";
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(
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
