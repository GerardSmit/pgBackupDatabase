using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace PgDbBackup.Tests;

/// <summary>
/// Test-side helpers that are not bound to a fixture or connection: path
/// generation and .bak byte-stream parsers used by multiple test classes.
/// </summary>
internal static class Helpers
{
    public const byte SectionMetadata = 0x01;
    public const byte SectionSchema = 0x02;
    public const byte SectionData = 0x03;
    public const byte SectionLogicalStream = 0x04;
    public const byte SectionWalSegments = 0x05;

    public static string BackupPath(string prefix = "pgdbbackup") =>
        $"/tmp/{prefix}_{Guid.NewGuid():N}.bak";

    public static async Task ExecAsync(NpgsqlConnection conn, string sql,
        int? timeoutSeconds = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (timeoutSeconds.HasValue)
            cmd.CommandTimeout = timeoutSeconds.Value;
        await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Iterates the section frames of a .bak file: each entry is
    /// (sectionType, sectionLength, dataOffset, data). data is the raw
    /// (possibly compressed/encrypted) section payload bytes.
    /// </summary>
    public static IEnumerable<(byte sectionType, byte[] data)> DecodeSections(byte[] file)
    {
        int offset = 5 + 2; // magic + version
        if (file.Length < offset + 4 + 32 + 5)
            yield break;

        uint headerLen = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset, 4));
        offset += 4 + (int)headerLen;

        int endOfSections = file.Length - 32 - 5;

        while (offset < endOfSections)
        {
            byte sectionType = file[offset];
            offset += 1;
            ulong sectionLen = BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(offset, 8));
            offset += 8;
            int sLen = checked((int)sectionLen);
            var data = new byte[sLen];
            Buffer.BlockCopy(file, offset, data, 0, sLen);
            offset += sLen;
            yield return (sectionType, data);
        }
    }

    /// <summary>
    /// Walks section frames without copying payloads. Useful when only
    /// the section types or lengths matter.
    /// </summary>
    public static IEnumerable<(byte sectionType, ulong length, int dataOffset)> WalkSections(byte[] file)
    {
        int offset = 5 + 2;
        uint headerLen = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset, 4));
        offset += 4 + (int)headerLen;
        int endOfSections = file.Length - 32 - 5;

        while (offset < endOfSections)
        {
            byte sectionType = file[offset];
            offset += 1;
            ulong sectionLen = BinaryPrimitives.ReadUInt64BigEndian(file.AsSpan(offset, 8));
            offset += 8;
            yield return (sectionType, sectionLen, offset);
            offset += checked((int)sectionLen);
        }
    }

    public static List<byte> ListSectionTypes(byte[] file)
    {
        var types = new List<byte>();
        foreach (var s in WalkSections(file))
            types.Add(s.sectionType);
        return types;
    }

    public static List<string> DecodeLogicalStream(byte[] data)
    {
        var frames = new List<string>();
        if (data.Length < 4) return frames;

        var count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4));
        var offset = 4;
        for (uint i = 0; i < count && offset + 4 <= data.Length; i++)
        {
            var len = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
            var iLen = checked((int)len);
            if (offset + iLen > data.Length) break;
            frames.Add(Encoding.UTF8.GetString(data, offset, iLen));
            offset += iLen;
        }

        return frames;
    }

    /// <summary>
    /// Parses the JSON header from raw .bak bytes and returns a detached
    /// JsonElement that survives disposal of the underlying document.
    /// </summary>
    public static JsonElement ReadHeader(byte[] file)
    {
        int offset = 7;
        uint headerLen = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(offset, 4));
        offset += 4;
        var json = Encoding.UTF8.GetString(file, offset, (int)headerLen);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    /// <summary>
    /// Decodes the per-entry path list out of a SIMPLE-mode DATA section
    /// payload (the section data passed in must already be unwrapped from
    /// its outer framing — see DecodeSections).
    /// </summary>
    public static List<string> DecodeDataEntryPaths(byte[] dataSection)
    {
        var paths = new List<string>();
        int offset = 0;
        if (dataSection.Length < 4) return paths;
        uint count = BinaryPrimitives.ReadUInt32BigEndian(dataSection.AsSpan(offset, 4));
        offset += 4;
        for (uint i = 0; i < count; i++)
        {
            if (offset + 2 > dataSection.Length) break;
            ushort pathLen = BinaryPrimitives.ReadUInt16BigEndian(dataSection.AsSpan(offset, 2));
            offset += 2;
            if (offset + pathLen > dataSection.Length) break;
            var path = Encoding.UTF8.GetString(dataSection, offset, pathLen);
            offset += pathLen;
            if (offset + 8 > dataSection.Length) break;
            ulong dataLen = BinaryPrimitives.ReadUInt64BigEndian(dataSection.AsSpan(offset, 8));
            offset += 8;
            offset += checked((int)dataLen);
            offset += 32; // SHA-256 per entry
            paths.Add(path);
        }
        return paths;
    }
}

/// <summary>
/// Backup-related extension methods on NpgsqlConnection. These wrap the
/// SQL calls so test classes don't repeat the parameter plumbing.
/// </summary>
internal static class BackupExtensions
{
    public static async Task SetModeFullAsync(this NpgsqlConnection conn)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT dbbackup.pg_dbbackup_set_mode(@db, 'full')";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task BackupFullAsync(this NpgsqlConnection conn,
        string path, bool compress = false, string? password = null,
        int commandTimeoutSeconds = 120)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'full', " +
            "compress := @compress, password := @password)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("compress", compress);
        cmd.Parameters.Add(new NpgsqlParameter("password", NpgsqlDbType.Text)
        {
            Value = (object?)password ?? DBNull.Value,
        });
        cmd.CommandTimeout = commandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task BackupDiffAsync(this NpgsqlConnection conn,
        string path, string basePath, bool compress = false,
        int commandTimeoutSeconds = 120)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'differential', " +
            "compress := @compress, base_filepath := @base)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("compress", compress);
        cmd.Parameters.AddWithValue("base", basePath);
        cmd.CommandTimeout = commandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task BackupLogAsync(this NpgsqlConnection conn,
        string path, string basePath, bool compress = false,
        int commandTimeoutSeconds = 120)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT dbbackup.pg_dbbackup(@db, @path, type := 'log', " +
            "compress := @compress, base_filepath := @base)";
        cmd.Parameters.AddWithValue("db", conn.Database!);
        cmd.Parameters.AddWithValue("path", path);
        cmd.Parameters.AddWithValue("compress", compress);
        cmd.Parameters.AddWithValue("base", basePath);
        cmd.CommandTimeout = commandTimeoutSeconds;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task ExecAsync(this NpgsqlConnection conn, string sql,
        int? timeoutSeconds = null)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (timeoutSeconds.HasValue)
            cmd.CommandTimeout = timeoutSeconds.Value;
        await cmd.ExecuteNonQueryAsync();
    }
}
