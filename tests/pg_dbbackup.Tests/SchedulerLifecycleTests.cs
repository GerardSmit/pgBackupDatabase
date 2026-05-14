using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// End-to-end coverage for create_schedule / alter_schedule /
/// pause_schedule / resume_schedule / drop_schedule and the
/// run_due_schedules dispatcher, including window_start/end behaviour and
/// cron vs every triggering.
/// </summary>
[Collection(S3StorageCollection.Name)]
public sealed class SchedulerLifecycleTests
{
    private readonly S3StorageFixture _fixture;

    public SchedulerLifecycleTests(S3StorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Create_Alter_Pause_Resume_Drop_Cycle()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "sched_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "sched-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await Configure(conn, setName, db);

            Guid id;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.create_schedule(@s, 'nightly', 'full', " +
                    "  cron := '0 3 * * *', timezone := 'UTC')";
                cmd.Parameters.AddWithValue("s", setName);
                id = (Guid)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
            }
            Assert.NotEqual(Guid.Empty, id);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.alter_schedule(@s, 'nightly', " +
                    "  cron := '*/5 * * * *')";
                cmd.Parameters.AddWithValue("s", setName);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await AssertScheduleAsync(conn, setName, "nightly", "*/5 * * * *", true);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT dbbackup.pause_schedule(@s, 'nightly')";
                cmd.Parameters.AddWithValue("s", setName);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await AssertScheduleAsync(conn, setName, "nightly", "*/5 * * * *", false);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT dbbackup.resume_schedule(@s, 'nightly')";
                cmd.Parameters.AddWithValue("s", setName);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
            await AssertScheduleAsync(conn, setName, "nightly", "*/5 * * * *", true);

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT dbbackup.drop_schedule(@s, 'nightly')";
                cmd.Parameters.AddWithValue("s", setName);
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT count(*) FROM dbbackup.backup_schedules " +
                    "WHERE backup_set = @s AND name = 'nightly'";
                cmd.Parameters.AddWithValue("s", setName);
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!);
            }
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task AlterSchedule_Raises_When_Missing()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        try
        {
            var ex = await Assert.ThrowsAsync<Npgsql.PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.alter_schedule('nope', 'never', cron := '* * * * *')";
                await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            });
            Assert.Contains("does not exist", ex.MessageText);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task RunDueSchedules_Every_Interval_Fires_Backup()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "every_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "every-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await Configure(conn, setName, db);
            await conn.ExecAsync(
                "CREATE TABLE q(id int PRIMARY KEY);" +
                "INSERT INTO q VALUES (1);");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.create_schedule(@s, 'fast', 'full', " +
                    "  every := interval '5 minutes')";
                cmd.Parameters.AddWithValue("s", setName);
                await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }

            var made = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_run_due_schedules(now())");
            Assert.Equal(1, made);

            // Second call within "every" window: skipped.
            var second = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_run_due_schedules(now())");
            Assert.Equal(0, second);

            // Call again at now() + 6 minutes: should fire again.
            var third = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_run_due_schedules(now() + interval '6 minutes')");
            Assert.Equal(1, third);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task RunDueSchedules_Window_Excludes_OutOfWindow()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var setName = "win_" + Guid.NewGuid().ToString("N")[..8];
        var bucket = "win-" + Guid.NewGuid().ToString("N")[..10];
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(conn, "minio", bucket, "p");
            await Configure(conn, setName, db);
            await conn.ExecAsync(
                "CREATE TABLE q(id int PRIMARY KEY);" +
                "INSERT INTO q VALUES (1);");

            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.create_schedule(@s, 'win', 'full', " +
                    "  cron := '* * * * *', timezone := 'UTC', " +
                    "  window_start := time '02:00', window_end := time '04:00')";
                cmd.Parameters.AddWithValue("s", setName);
                await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            }

            var outOfWindow = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_run_due_schedules('2026-05-13 10:00:00+00'::timestamptz)");
            Assert.Equal(0, outOfWindow);

            var inWindow = await ScalarIntAsync(conn,
                "SELECT dbbackup.pg_dbbackup_run_due_schedules('2026-05-13 02:30:00+00'::timestamptz)");
            Assert.Equal(1, inWindow);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    // ----- helpers -----

    private static async Task Configure(
        Npgsql.NpgsqlConnection conn, string set, string db)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.create_backup_set(@s, 'minio'); " +
            "SELECT dbbackup.add_database_to_backup_set(@s, @d)";
        cmd.Parameters.AddWithValue("s", set);
        cmd.Parameters.AddWithValue("d", db);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task AssertScheduleAsync(
        Npgsql.NpgsqlConnection conn, string set, string name,
        string expectedCron, bool expectedEnabled)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT cron, enabled FROM dbbackup.backup_schedules " +
            "WHERE backup_set = @s AND name = @n";
        cmd.Parameters.AddWithValue("s", set);
        cmd.Parameters.AddWithValue("n", name);
        await using var rdr = await cmd.ExecuteReaderAsync(
            TestContext.Current.CancellationToken);
        Assert.True(await rdr.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expectedCron, rdr.GetString(0));
        Assert.Equal(expectedEnabled, rdr.GetBoolean(1));
    }

    private static async Task<int> ScalarIntAsync(
        Npgsql.NpgsqlConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = 120;
        return (int)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
