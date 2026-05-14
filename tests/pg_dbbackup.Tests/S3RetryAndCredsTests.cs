using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

[Collection(S3StorageCollection.Name)]
public sealed class S3RetryAndCredsTests
{
    private readonly S3StorageFixture _fixture;

    public S3RetryAndCredsTests(S3StorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Backup_Surfaces_Network_Failure_After_Retries()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "ret-" + Guid.NewGuid().ToString("N")[..10].ToLowerInvariant();
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.create_s3_target(" +
                    "name := 'unreach', bucket := @bucket, prefix := 'p', " +
                    "region := 'us-east-1', " +
                    "endpoint_url := 'http://127.0.0.1:1', " +
                    "force_path_style := true, max_retries := 2, " +
                    "request_timeout_ms := 1500)";
                cmd.Parameters.AddWithValue("bucket", bucket);
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }

            await conn.ExecAsync(
                "CREATE TABLE big(id int PRIMARY KEY, payload text);" +
                "INSERT INTO big " +
                "SELECT g, repeat(md5(g::text), 32) " +
                "FROM generate_series(1, 200) g;");

            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage(" +
                    "dbname := @db, type := 'full', " +
                    "storage_target := 'unreach', compress := false)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.CommandTimeout = 120;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            });
            Assert.NotNull(ex.SqlState);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task Backup_To_Bucket_Removed_Underneath_Errors()
    {
        // Represents the 403/expired-creds class of failures: bucket
        // suddenly inaccessible during upload. Surface error must be a
        // clean PG exception with an SqlState rather than a hang or crash.
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "gone-" + Guid.NewGuid().ToString("N")[..10].ToLowerInvariant();
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.create_s3_target(" +
                    "name := 'gone', bucket := @bucket, prefix := 'p', " +
                    "region := 'us-east-1', " +
                    "endpoint_url := 'http://minio:9000', " +
                    "force_path_style := true, max_retries := 1, " +
                    "request_timeout_ms := 5000)";
                cmd.Parameters.AddWithValue("bucket", bucket);
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            await conn.ExecAsync("CREATE TABLE t(id int PRIMARY KEY);");

            // No bucket created. PUT against missing bucket maps to a
            // NoSuchBucket / 404 response.
            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage(" +
                    "dbname := @db, type := 'full', " +
                    "storage_target := 'gone', compress := false)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.CommandTimeout = 60;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            });
            Assert.NotNull(ex.SqlState);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }
}
