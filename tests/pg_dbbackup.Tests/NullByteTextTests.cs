using Npgsql;
using PgDbBackup.Tests.Fixtures;
using Xunit;

namespace PgDbBackup.Tests;

/// <summary>
/// Text values with embedded special characters: newlines, tabs,
/// backslashes, quote characters, and Unicode escapes. The
/// COPY/serializer path must not corrupt these. (NUL bytes in text
/// columns are forbidden by PG so we skip that — bytea covered
/// elsewhere via LargeByteaTests.)
/// </summary>
public sealed class NullByteTextTests
{
    private readonly PgContainerFixture _pg;

    public NullByteTextTests(PgContainerFixture pg) => _pg = pg;

    [Fact]
    public async Task Special_Char_Text_Values_RoundTrip_Byte_For_Byte()
    {
        await using var src = await _pg.CreateFreshDbWithExtensionAsync();
        await src.ExecAsync(
            "CREATE TABLE sct(id int PRIMARY KEY, payload text);");

        var values = new (int Id, string Payload)[]
        {
            (1, "line1\nline2\nline3"),
            (2, "tab\there\tand\there"),
            (3, "back\\slash and \"quote\" and 'apos'"),
            (4, "unicode: ☃ ☎ 🎉 — em-dash"),
            (5, "CRLF\r\nmix\rCR\nLF"),
            (6, "  leading & trailing spaces  "),
            (7, ""),  // empty string
            (8, "json: {\"k\":\"v\",\"arr\":[1,2,3]}"),
        };

        foreach (var v in values)
        {
            await using var cmd = src.CreateCommand();
            cmd.CommandText = "INSERT INTO sct(id, payload) VALUES (@i, @p)";
            cmd.Parameters.AddWithValue("i", v.Id);
            cmd.Parameters.AddWithValue("p", v.Payload);
            await cmd.ExecuteNonQueryAsync();
        }

        var full = Helpers.BackupPath("sct");
        await src.BackupFullAsync(full, compress: false);
        await src.CloseAsync();

        var target = "sct_" + Guid.NewGuid().ToString("N")[..8];
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
            foreach (var v in values)
            {
                await using var c = r.CreateCommand();
                c.CommandText = "SELECT payload FROM sct WHERE id = @i";
                c.Parameters.AddWithValue("i", v.Id);
                var got = (string)(await c.ExecuteScalarAsync(
                    TestContext.Current.CancellationToken))!;
                Assert.Equal(v.Payload, got);
            }
        }
        finally
        {
            try { await _pg.DropDbAsync(target); } catch { }
        }
    }
}
