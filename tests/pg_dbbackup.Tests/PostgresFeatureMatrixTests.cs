using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class PostgresFeatureMatrixTests
{
    private readonly PgContainerFixture _pg;

    public PostgresFeatureMatrixTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task FullRestore_Replays_TriggerSideEffects_Without_DoubleFiring()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE items(id int PRIMARY KEY, name text NOT NULL);" +
            "CREATE TABLE audit(" +
            "  audit_id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY," +
            "  item_id int NOT NULL," +
            "  action text NOT NULL" +
            ");" +
            "CREATE FUNCTION audit_item_insert() RETURNS trigger LANGUAGE plpgsql AS $$" +
            "BEGIN " +
            "  INSERT INTO audit(item_id, action) VALUES (NEW.id, TG_OP); " +
            "  RETURN NEW; " +
            "END $$;" +
            "CREATE TRIGGER items_ai AFTER INSERT ON items " +
            "FOR EACH ROW EXECUTE FUNCTION audit_item_insert();" +
            "INSERT INTO items VALUES (1, 'full');");

        var full = Helpers.BackupPath("trigger_matrix");
        await src.BackupFullAsync(full);

        await src.ExecAsync("INSERT INTO items VALUES (2, 'log');");
        var log = Helpers.BackupPath("trigger_matrix");
        await src.BackupLogAsync(log, full);
        await src.CloseAsync();

        var target = "trigger_matrix_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT array_agg(item_id ORDER BY item_id) FROM audit";
                var audited = (int[])(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
                Assert.Equal(new[] { 1, 2 }, audited);
            }

            await conn.ExecAsync("INSERT INTO items VALUES (3, 'after restore');");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM audit WHERE item_id = 3 AND action = 'INSERT'";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_RoundTrips_Partitioned_Table_And_PostFull_Partition_Ddl()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE events(" +
            "  day date NOT NULL," +
            "  id int NOT NULL," +
            "  payload text NOT NULL," +
            "  PRIMARY KEY(day, id)" +
            ") PARTITION BY RANGE(day);" +
            "CREATE TABLE events_2026_01 PARTITION OF events " +
            "FOR VALUES FROM ('2026-01-01') TO ('2026-02-01');" +
            "INSERT INTO events VALUES ('2026-01-10', 1, 'full');");

        var full = Helpers.BackupPath("partition_matrix");
        await src.BackupFullAsync(full);

        await src.ExecAsync(
            "CREATE TABLE events_2026_02 PARTITION OF events " +
            "FOR VALUES FROM ('2026-02-01') TO ('2026-03-01');" +
            "INSERT INTO events VALUES ('2026-02-10', 2, 'log');");
        var log = Helpers.BackupPath("partition_matrix");
        await src.BackupLogAsync(log, full);
        await src.CloseAsync();

        var target = "partition_matrix_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full, log);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM events";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM pg_inherits WHERE inhparent = 'events'::regclass";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await conn.ExecAsync("INSERT INTO events VALUES ('2026-02-20', 3, 'routed');");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM events_2026_02";
                Assert.Equal(2L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_RoundTrips_Rls_Identity_Generated_Domain_And_Enum()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TYPE mood AS ENUM ('sad', 'ok', 'happy');" +
            "CREATE DOMAIN email_text AS text CHECK (position('@' in VALUE) > 1);" +
            "CREATE TABLE accounts(" +
            "  id int GENERATED ALWAYS AS IDENTITY PRIMARY KEY," +
            "  email email_text NOT NULL," +
            "  status mood NOT NULL DEFAULT 'ok'," +
            "  qty int NOT NULL," +
            "  doubled int GENERATED ALWAYS AS (qty * 2) STORED" +
            ");" +
            "ALTER TABLE accounts ENABLE ROW LEVEL SECURITY;" +
            "CREATE POLICY accounts_public_select ON accounts " +
            "FOR SELECT TO PUBLIC USING (qty > 0);" +
            "INSERT INTO accounts(email, qty) VALUES ('a@example.com', 5);");

        var full = Helpers.BackupPath("core_matrix");
        await src.BackupFullAsync(full);
        await src.CloseAsync();

        var target = "core_matrix_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT id, doubled, status::text FROM accounts";
                await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(1, rdr.GetInt32(0));
                Assert.Equal(10, rdr.GetInt32(1));
                Assert.Equal("ok", rdr.GetString(2));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT c.relrowsecurity, count(p.*) " +
                    "FROM pg_class c " +
                    "LEFT JOIN pg_policy p ON p.polrelid = c.oid " +
                    "WHERE c.oid = 'accounts'::regclass " +
                    "GROUP BY c.relrowsecurity";
                await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.True(rdr.GetBoolean(0));
                Assert.Equal(1L, rdr.GetInt64(1));
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "INSERT INTO accounts(email, qty) VALUES ('invalid', 1)";
                var ex = await Assert.ThrowsAsync<PostgresException>(
                    () => cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
                Assert.Equal("23514", ex.SqlState);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "INSERT INTO accounts(email, qty) VALUES ('b@example.com', 7) " +
                    "RETURNING id, doubled";
                await using var rdr = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
                Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
                Assert.Equal(3, rdr.GetInt32(0));
                Assert.Equal(14, rdr.GetInt32(1));
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullRestore_RoundTrips_Views_And_Rewrite_Rules()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE rule_source(id int PRIMARY KEY, active bool NOT NULL);" +
            "CREATE TABLE rule_log(id int PRIMARY KEY);" +
            "CREATE VIEW active_rule_source AS " +
            "SELECT id FROM rule_source WHERE active;" +
            "CREATE RULE rule_source_log_insert AS " +
            "ON INSERT TO rule_source DO ALSO " +
            "INSERT INTO rule_log VALUES (NEW.id);" +
            "INSERT INTO rule_source VALUES (1, true);");

        var full = Helpers.BackupPath("rules_matrix");
        await src.BackupFullAsync(full);
        await src.CloseAsync();

        var target = "rules_matrix_" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            await RestoreAsync(target, full);

            await using var conn = await _pg.ConnectToAsync(target);
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM active_rule_source";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await conn.ExecAsync("INSERT INTO rule_source VALUES (2, false);");
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT count(*) FROM rule_log WHERE id = 2";
                Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }

    [Fact]
    public async Task FullBackup_Rejects_Unlogged_Tables()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync("CREATE UNLOGGED TABLE volatile_items(id int PRIMARY KEY);");

        await AssertFullBackupRejectedAsync(src, "unlogged_matrix", "unlogged tables");
    }

    [Fact]
    public async Task FullBackup_Rejects_Foreign_Tables()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE EXTENSION file_fdw;" +
            "CREATE SERVER pgdb_file FOREIGN DATA WRAPPER file_fdw;" +
            "CREATE FOREIGN TABLE imported_rows(id int) SERVER pgdb_file " +
            "OPTIONS (filename '/tmp/pgdbbackup_missing.csv', format 'csv');");

        await AssertFullBackupRejectedAsync(src, "foreign_matrix", "foreign tables");
    }

    [Fact]
    public async Task FullBackup_Rejects_Regular_Materialized_Views()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE mv_source(id int PRIMARY KEY);" +
            "CREATE MATERIALIZED VIEW mv_source_snapshot AS SELECT * FROM mv_source;");

        await AssertFullBackupRejectedAsync(src, "matview_matrix", "materialized views");
    }

    [Fact]
    public async Task FullBackup_Rejects_Ordinary_Table_Inheritance()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE inherited_parent(id int PRIMARY KEY);" +
            "CREATE TABLE inherited_child(extra text) INHERITS (inherited_parent);");

        await AssertFullBackupRejectedAsync(src, "inheritance_matrix", "ordinary table inheritance");
    }

    [Fact]
    public async Task FullBackup_Rejects_User_Event_Triggers()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE FUNCTION user_event_trigger_fn() RETURNS event_trigger " +
            "LANGUAGE plpgsql AS $$ BEGIN NULL; END; $$;" +
            "CREATE EVENT TRIGGER user_event_trigger " +
            "ON ddl_command_end EXECUTE FUNCTION user_event_trigger_fn();");

        await AssertFullBackupRejectedAsync(src, "event_trigger_matrix", "event triggers");
    }

    [Fact]
    public async Task FullBackup_Rejects_Custom_Range_Types()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TYPE custom_float_range AS RANGE (subtype = float8);");

        await AssertFullBackupRejectedAsync(src, "range_type_matrix", "range/base/pseudo types");
    }

    [Fact]
    public async Task FullBackup_Rejects_Custom_Text_Search_Configurations()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TEXT SEARCH CONFIGURATION public.custom_simple_cfg " +
            "(COPY = pg_catalog.simple);");

        await AssertFullBackupRejectedAsync(
            src, "textsearch_matrix", "text search configurations");
    }

    [Fact]
    public async Task FullBackup_Rejects_User_Aggregates()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE FUNCTION custom_sum_sfunc(state int, val int) " +
            "RETURNS int LANGUAGE sql IMMUTABLE AS " +
            "$$ SELECT coalesce(state, 0) + val $$;" +
            "CREATE AGGREGATE custom_sum(int) (" +
            "  SFUNC = custom_sum_sfunc," +
            "  STYPE = int," +
            "  INITCOND = '0'" +
            ");");

        await AssertFullBackupRejectedAsync(src, "aggregate_matrix", "aggregates");
    }

    [Fact]
    public async Task FullBackup_Rejects_Logical_Publications()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE TABLE published_items(id int PRIMARY KEY);" +
            "CREATE PUBLICATION published_items_pub FOR TABLE published_items;");

        await AssertFullBackupRejectedAsync(src, "publication_matrix", "publications");
    }

    [Fact]
    public async Task FullBackup_Rejects_Logical_Subscriptions()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.SetModeFullAsync();
        await src.ExecAsync(
            "CREATE SUBSCRIPTION detached_sub " +
            "CONNECTION 'host=localhost port=1 dbname=postgres' " +
            "PUBLICATION missing_pub " +
            "WITH (connect = false, enabled = false);");

        await AssertFullBackupRejectedAsync(src, "subscription_matrix", "subscriptions");
    }

    private static async Task AssertFullBackupRejectedAsync(
        NpgsqlConnection conn, string prefix, string expectedMessage)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(
            () => conn.BackupFullAsync(Helpers.BackupPath(prefix)));
        Assert.Equal("0A000", ex.SqlState);
        Assert.Contains(expectedMessage, ex.MessageText,
            StringComparison.OrdinalIgnoreCase);
    }

    private async Task RestoreAsync(string target, params string[] files)
    {
        await using var admin = await _pg.AdminAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbrestore(@db, @files::text[], target_db := @target)";
        cmd.Parameters.AddWithValue("db", "ignored");
        cmd.Parameters.AddWithValue("files", files);
        cmd.Parameters.AddWithValue("target", target);
        cmd.CommandTimeout = 300;
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        NpgsqlConnection.ClearAllPools();
    }
}
