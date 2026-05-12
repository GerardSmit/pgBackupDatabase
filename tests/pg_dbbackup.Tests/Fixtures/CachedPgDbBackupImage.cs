using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PgDbBackup.Tests.Fixtures;

internal static class CachedPgDbBackupImage
{
    private const string DockerfileName = "Dockerfile.pg_dbbackup_test";
    private static readonly SemaphoreSlim BuildLock = new(1, 1);

    public static async Task<string> BuildAsync(string baseImage, string pgMajor)
    {
        var root = PgContainerFixture.ProjectRoot;
        var hash = ComputeBuildHash(root, baseImage, pgMajor);
        var tag = $"pgdbbackup-test:{Sanitize(baseImage)}-pg{pgMajor}-{hash[..16]}";

        if (await DockerImageExistsAsync(tag))
        {
            return tag;
        }

        await BuildLock.WaitAsync();
        try
        {
            if (await DockerImageExistsAsync(tag))
            {
                return tag;
            }

            var result = await DockerAsync(root,
                "build",
                "--progress=plain",
                "--build-arg", $"BASE_IMAGE={baseImage}",
                "--build-arg", $"PG_MAJOR={pgMajor}",
                "-f", Path.Combine(root, DockerfileName),
                "-t", tag,
                root);

            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"docker build failed for {tag} (exit {result.ExitCode}):\n" +
                    $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
            }

            return tag;
        }
        finally
        {
            BuildLock.Release();
        }
    }

    public static async Task<string> DetectPgMajorAsync(string image)
    {
        var result = await DockerAsync(PgContainerFixture.ProjectRoot,
            "run", "--rm", "--entrypoint", "sh", image,
            "-c", "pg_config --version | awk '{print $2}' | cut -d. -f1");

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"could not detect PostgreSQL major for {image}:\n" +
                $"stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");
        }

        return result.Stdout.Trim();
    }

    private static async Task<bool> DockerImageExistsAsync(string tag)
    {
        var result = await DockerAsync(PgContainerFixture.ProjectRoot,
            "image", "inspect", tag);
        return result.ExitCode == 0;
    }

    private static string ComputeBuildHash(string root, string baseImage, string pgMajor)
    {
        using var sha = SHA256.Create();

        AddString(sha, $"base={baseImage}\npg={pgMajor}\n");
        AddFile(sha, root, DockerfileName);
        AddFile(sha, root, ".dockerignore");
        AddFile(sha, root, "Makefile");
        AddFile(sha, root, "pg_dbbackup.control");

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            AddFile(sha, root, Path.GetRelativePath(root, path));
        }

        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "sql"), "*.sql", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            AddFile(sha, root, Path.GetRelativePath(root, path));
        }

        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return Convert.ToHexString(sha.Hash!).ToLowerInvariant();
    }

    private static void AddFile(HashAlgorithm sha, string root, string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/');
        AddString(sha, $"file:{normalized}\n");
        var bytes = File.ReadAllBytes(Path.Combine(root, relativePath));
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
        AddString(sha, "\n");
    }

    private static void AddString(HashAlgorithm sha, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
    }

    private static string Sanitize(string value)
    {
        var chars = value.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        return new string(chars).Trim('-');
    }

    private static async Task<ProcessResult> DockerAsync(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var p = Process.Start(psi)
            ?? throw new InvalidOperationException("failed to start docker");

        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();

        return new ProcessResult(
            p.ExitCode,
            await stdout,
            await stderr);
    }

    private readonly record struct ProcessResult(int ExitCode, string Stdout, string Stderr);
}
