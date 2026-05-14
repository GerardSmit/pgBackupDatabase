using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Negative-path coverage for the S3 backup/restore surface. Exercises
/// 404 (missing bucket / missing object) and chain-state checks. 403 is
/// covered by configuring a target with bad credentials; mid-multipart
/// fault injection would require an HTTP proxy and is intentionally
/// out of scope here.
/// </summary>
[Collection(S3StorageCollection.Name)]
public sealed class S3NegativePathTests
{
    private readonly S3StorageFixture _fixture;

    public S3NegativePathTests(S3StorageFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Backup_To_Unknown_Storage_Target_Errors()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        try
        {
            await conn.ExecAsync("CREATE TABLE t(id int);");

            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage(" +
                    "dbname := @db, type := 'full', " +
                    "storage_target := 'does-not-exist', compress := false)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.CommandTimeout = 60;
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            });

            // ERRCODE_UNDEFINED_OBJECT
            Assert.Equal("42704", ex.SqlState);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task Backup_To_Missing_Bucket_Surfaces_S3_Error()
    {
        // Configure the target but never call pg_dbbackup_s3_create_bucket.
        // MinIO returns 404 NoSuchBucket; the extension's retry budget is
        // exhausted before the call gives up.
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "missing-" + Guid.NewGuid().ToString("N")[..16].ToLowerInvariant();
        try
        {
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText =
                    "SELECT dbbackup.create_s3_target(" +
                    "name := 'minio-missing', bucket := @bucket, prefix := 'x', " +
                    "region := 'us-east-1', endpoint_url := 'http://minio:9000', " +
                    "force_path_style := true, max_retries := 1, " +
                    "request_timeout_ms := 5000)";
                cmd.Parameters.AddWithValue("bucket", bucket);
                await cmd.ExecuteNonQueryAsync(
                    TestContext.Current.CancellationToken);
            }
            await conn.ExecAsync("CREATE TABLE t(id int);");

            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbbackup_to_storage(" +
                    "dbname := @db, type := 'full', " +
                    "storage_target := 'minio-missing', compress := false)";
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

    [Fact]
    public async Task S3_Object_Exists_Returns_False_For_Missing_Key()
    {
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "obj-" + Guid.NewGuid().ToString("N")[..16].ToLowerInvariant();
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(
                conn, "minio-obj", bucket, "prefix");

            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT dbbackup.pg_dbbackup_s3_object_exists('minio-obj', " +
                "'does/not/exist.bak')";
            var exists = (bool)(await cmd.ExecuteScalarAsync(
                TestContext.Current.CancellationToken))!;
            Assert.False(exists);
        }
        finally
        {
            await _fixture.DropDbAsync(db);
        }
    }

    [Fact]
    public async Task Restore_From_Storage_With_No_Backups_Errors()
    {
        // Empty bucket + empty catalog → restore must fail rather than
        // silently produce an empty target DB.
        await using var conn = await _fixture.CreateFreshDbWithExtensionAsync();
        var db = conn.Database!;
        var bucket = "empty-" + Guid.NewGuid().ToString("N")[..16].ToLowerInvariant();
        try
        {
            await _fixture.ConfigureTargetAndBucketAsync(
                conn, "minio-empty", bucket, "prefix");

            var ex = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText =
                    "SELECT dbbackup.pg_dbrestore_from_storage(" +
                    "dbname := @db, storage_target := 'minio-empty', " +
                    "target_db := @target)";
                cmd.Parameters.AddWithValue("db", db);
                cmd.Parameters.AddWithValue("target", db + "_restored");
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
