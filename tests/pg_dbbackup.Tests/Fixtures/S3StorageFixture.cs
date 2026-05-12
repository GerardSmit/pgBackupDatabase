using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace PgDbBackup.Tests.Fixtures;

public sealed class S3StorageFixture : IAsyncLifetime
{
    private const string PostgresImage = "postgres:17";
    private const string PostgresUser = "postgres";
    private const string PostgresPassword = "postgres";
    private const string PostgresDb = "postgres";
    private const string MinioImage = "minio/minio:latest";
    private const string AccessKey = "minioadmin";
    private const string SecretKey = "minioadmin";

    private INetwork _network = null!;
    private IContainer _minio = null!;
    private PostgreSqlContainer _postgres = null!;

    public string ConnectionString => _postgres.GetConnectionString();

    public async ValueTask InitializeAsync()
    {
        var image = await CachedPgDbBackupImage.BuildAsync(PostgresImage, "17");

        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _minio = new ContainerBuilder()
            .WithImage(MinioImage)
            .WithEnvironment("MINIO_ROOT_USER", AccessKey)
            .WithEnvironment("MINIO_ROOT_PASSWORD", SecretKey)
            .WithCommand("server", "/data", "--address", ":9000")
            .WithNetwork(_network)
            .WithNetworkAliases("minio")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(9000))
            .Build();

        await _minio.StartAsync();

        _postgres = new PostgreSqlBuilder()
            .WithImage(image)
            .WithUsername(PostgresUser)
            .WithPassword(PostgresPassword)
            .WithDatabase(PostgresDb)
            .WithEnvironment("AWS_ACCESS_KEY_ID", AccessKey)
            .WithEnvironment("AWS_SECRET_ACCESS_KEY", SecretKey)
            .WithEnvironment("AWS_REGION", "us-east-1")
            .WithNetwork(_network)
            .WithNetworkAliases("pg")
            .WithCommand(
                "-c", "shared_preload_libraries=pg_dbbackup",
                "-c", "wal_level=logical",
                "-c", "max_replication_slots=100",
                "-c", "track_commit_timestamp=on",
                "-c", "max_wal_senders=5",
                "-c", "wal_keep_size=64",
                "-c", "summarize_wal=on")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .AddCustomWaitStrategy(new ImmediateReadyWait()))
            .Build();

        await _postgres.StartAsync();
        await WaitForReadyConnectionAsync();
        await EnsureExtensionInPostgresDbAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_postgres is not null)
            await _postgres.DisposeAsync();
        if (_minio is not null)
            await _minio.DisposeAsync();
        if (_network is not null)
            await _network.DisposeAsync();
    }

    public NpgsqlConnection CreateConnection() => new(ConnectionString);

    public async Task<NpgsqlConnection> AdminAsync()
    {
        var conn = CreateConnection();
        await conn.OpenAsync();
        return conn;
    }

    public async Task<NpgsqlConnection> ConnectToAsync(string dbName)
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = dbName,
        };
        var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task<NpgsqlConnection> CreateFreshDbWithExtensionAsync()
    {
        var dbName = "test_" + Guid.NewGuid().ToString("N")[..8];

        await using (var admin = CreateConnection())
        {
            await admin.OpenAsync();
            await using var cmd = admin.CreateCommand();
            cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var conn = await ConnectToAsync(dbName);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "CREATE EXTENSION pg_dbbackup";
            await cmd.ExecuteNonQueryAsync();
        }
        return conn;
    }

    public async Task DropDbAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        await using var admin = CreateConnection();
        await admin.OpenAsync();
        await using var cmd = admin.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        cmd.CommandTimeout = 60;
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task ConfigureTargetAndBucketAsync(
        NpgsqlConnection conn,
        string targetName,
        string bucket,
        string prefix)
    {
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText =
                "SELECT dbbackup.create_s3_target(" +
                "name := @name, bucket := @bucket, prefix := @prefix, " +
                "region := 'us-east-1', endpoint_url := 'http://minio:9000', " +
                "force_path_style := true)";
            cmd.Parameters.AddWithValue("name", targetName);
            cmd.Parameters.AddWithValue("bucket", bucket);
            cmd.Parameters.AddWithValue("prefix", prefix);
            await cmd.ExecuteNonQueryAsync();
        }

        await RetryAsync(async () =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT dbbackup.pg_dbbackup_s3_create_bucket(@target)";
            cmd.Parameters.AddWithValue("target", targetName);
            await cmd.ExecuteNonQueryAsync();
        });
    }

    private async Task EnsureExtensionInPostgresDbAsync()
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_dbbackup";
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task WaitForReadyConnectionAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await using var c = CreateConnection();
                await c.OpenAsync();
                await using var cmd = c.CreateCommand();
                cmd.CommandText = "SELECT 1";
                await cmd.ExecuteScalarAsync();
                return;
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(1000);
            }
        }

        throw new InvalidOperationException(
            $"PostgreSQL not accepting connections on {ConnectionString}", last);
    }

    private static async Task RetryAsync(Func<Task> action)
    {
        Exception? last = null;
        for (var i = 0; i < 20; i++)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception e)
            {
                last = e;
                await Task.Delay(500);
            }
        }

        throw new InvalidOperationException("operation did not succeed after retries", last);
    }
}

[CollectionDefinition(Name)]
public sealed class S3StorageCollection : ICollectionFixture<S3StorageFixture>
{
    public const string Name = "s3-storage";
}
