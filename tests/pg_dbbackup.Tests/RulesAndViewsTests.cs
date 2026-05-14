using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// CREATE RULE attached to a table changes INSERT/UPDATE/DELETE
/// semantics. Restored rules must fire identically. Views built ON
/// rules are themselves rewritten at parse time; both layers must
/// round-trip.
/// </summary>
public sealed class RulesAndViewsTests
{
    private readonly PgContainerFixture _pg;

    public RulesAndViewsTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Rule_Redirecting_Insert_RoundTrips()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE rule_t(id int PRIMARY KEY, v text);" +
            "CREATE TABLE rule_audit(id int, v text, at timestamptz DEFAULT now());" +
            "CREATE RULE rule_t_log AS ON INSERT TO rule_t " +
            "  DO ALSO INSERT INTO rule_audit(id, v) VALUES (NEW.id, NEW.v);" +
            "INSERT INTO rule_t VALUES (1, 'first'), (2, 'second');");

        var full = Helpers.BackupPath("rule");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "rule_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await using var admin = await _pg.AdminAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbrestore(@files::text[], target_db := @tgt)";
            cmd.Parameters.AddWithValue("files", new[] { full });
            cmd.Parameters.AddWithValue("tgt", target);
            cmd.CommandTimeout = 120;
            await cmd.ExecuteNonQueryAsync(
                TestContext.Current.CancellationToken);
            NpgsqlConnection.ClearAllPools();

            await using var r = await _pg.ConnectToAsync(target);

            // Rule is restored.
            await using (var c = r.CreateCommand())
            {
                c.CommandText =
                    "SELECT count(*) FROM pg_rewrite " +
                    "WHERE rulename = 'rule_t_log'";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }

            // Rule fires on new INSERT, populating the audit table.
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "INSERT INTO rule_t VALUES (3, 'third')";
                await c.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            await using (var c = r.CreateCommand())
            {
                c.CommandText = "SELECT count(*) FROM rule_audit WHERE id = 3";
                Assert.Equal(1L, (long)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
