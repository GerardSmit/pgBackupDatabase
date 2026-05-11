using System.Text;
using Npgsql;
using NpgsqlTypes;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

public sealed class CryptoTests
{
    private readonly PgContainerFixture _pg;

    public CryptoTests(PgContainerFixture pg) => _pg = pg;

    private static byte[] BuildRepeatingPayload(int targetBytes)
    {
        var unit = Encoding.ASCII.GetBytes("abcd");
        var buf = new byte[targetBytes];
        for (int i = 0; i < targetBytes; i++)
            buf[i] = unit[i % unit.Length];
        return buf;
    }

    private static async Task<byte[]> RunTestCryptoAsync(
        NpgsqlConnection conn, byte[] input, bool compress, string? password)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup_test_crypto(@input, @compress, @password)";
        cmd.Parameters.Add(new NpgsqlParameter("input", NpgsqlDbType.Bytea) { Value = input });
        cmd.Parameters.Add(new NpgsqlParameter("compress", NpgsqlDbType.Boolean) { Value = compress });
        cmd.Parameters.Add(new NpgsqlParameter("password", NpgsqlDbType.Text)
        {
            Value = (object?)password ?? DBNull.Value
        });

        var result = await cmd.ExecuteScalarAsync();
        Assert.NotNull(result);
        return (byte[])result!;
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    [Fact]
    public async Task Compressed_Output_Smaller_Than_Input()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var input = BuildRepeatingPayload(4096);

        var output = await RunTestCryptoAsync(conn, input, compress: true, password: null);

        Assert.True(output.Length < 200,
            $"expected compressed output < 200 bytes, got {output.Length}");
    }

    [Fact]
    public async Task Encrypted_Output_Not_Plaintext()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var input = BuildRepeatingPayload(4096);

        var output = await RunTestCryptoAsync(conn, input, compress: false, password: "secret");

        Assert.False(ContainsSubsequence(output, input),
            "encrypted output unexpectedly contains the plaintext as a substring");

        // Spot-check that a 16-byte run of "abcd" doesn't appear either,
        // which would indicate the cipher passed plaintext through.
        var run = BuildRepeatingPayload(16);
        Assert.False(ContainsSubsequence(output, run),
            "encrypted output contains a 16-byte run of plaintext pattern");
    }

    [Fact]
    public async Task Same_Password_Different_Ciphertext()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var input = BuildRepeatingPayload(1024);

        var first = await RunTestCryptoAsync(conn, input, compress: false, password: "secret");
        var second = await RunTestCryptoAsync(conn, input, compress: false, password: "secret");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Compressed_Then_Encrypted_Combined()
    {
        await using var conn = await _pg.CreateFreshDbWithExtensionAsync();
        var input = BuildRepeatingPayload(4096);

        var output = await RunTestCryptoAsync(conn, input, compress: true, password: "secret");

        Assert.NotNull(output);
        Assert.True(output.Length > 0);
        // Internal roundtrip assertion lives in the C function; reaching here
        // means decrypt + decompress recovered the original bytes.
    }
}
