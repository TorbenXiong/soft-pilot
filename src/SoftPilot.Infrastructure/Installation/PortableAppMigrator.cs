using System.Security.Cryptography;

namespace SoftPilot.Infrastructure.Installation;

public sealed class PortableAppMigrator
{
    public static string GetCurrentApplicationPath() => NormalizeFile(
        Environment.ProcessPath
        ?? throw new InvalidOperationException("无法确定 SoftPilot.exe 的当前位置。"));

    public static string GetCurrentApplicationRoot() =>
        Path.GetDirectoryName(GetCurrentApplicationPath())
        ?? throw new InvalidOperationException("无法确定 SoftPilot.exe 所在目录。");

    public async Task MigrateAsync(
        string sourceExecutable,
        string targetExecutable,
        CancellationToken cancellationToken = default)
    {
        sourceExecutable = NormalizeFile(sourceExecutable);
        targetExecutable = NormalizeFile(targetExecutable);
        if (PathsEqual(sourceExecutable, targetExecutable))
        {
            return;
        }

        if (!File.Exists(sourceExecutable))
        {
            throw new SoftPilotException($"找不到便携应用本体：{sourceExecutable}");
        }

        var targetRoot = Path.GetDirectoryName(targetExecutable)!;
        var transactionId = Guid.NewGuid().ToString("N");
        var incoming = Path.Combine(
            Path.GetDirectoryName(targetRoot)!,
            $".{Path.GetFileName(targetRoot)}.incoming-{transactionId}.exe");
        var previous = Path.Combine(targetRoot, $".SoftPilot.previous-{transactionId}.exe");
        var movedPrevious = false;
        var committed = false;
        try
        {
            await CopyFileAsync(sourceExecutable, incoming, cancellationToken);
            var sourceHash = await ComputeHashAsync(sourceExecutable, cancellationToken);
            var incomingHash = await ComputeHashAsync(incoming, cancellationToken);
            if (!string.Equals(sourceHash, incomingHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new SoftPilotException("应用本体迁移后的 SHA-256 校验失败。");
            }

            Directory.CreateDirectory(targetRoot);
            if (File.Exists(targetExecutable))
            {
                File.Move(targetExecutable, previous);
                movedPrevious = true;
            }

            File.Move(incoming, targetExecutable);
            committed = true;
        }
        catch
        {
            if (committed && File.Exists(targetExecutable))
            {
                File.Delete(targetExecutable);
            }

            if (movedPrevious && File.Exists(previous) && !File.Exists(targetExecutable))
            {
                File.Move(previous, targetExecutable);
            }

            throw;
        }
        finally
        {
            DeleteIfPresent(incoming);
            if (committed)
            {
                DeleteIfPresent(previous);
            }
        }
    }

    public async Task CleanupSourceExecutableAsync(
        string sourceExecutable,
        string targetExecutable,
        CancellationToken cancellationToken = default)
    {
        sourceExecutable = NormalizeFile(sourceExecutable);
        targetExecutable = NormalizeFile(targetExecutable);
        if (PathsEqual(sourceExecutable, targetExecutable) || !File.Exists(sourceExecutable))
        {
            return;
        }

        if (!File.Exists(targetExecutable))
        {
            throw new SoftPilotException("迁移后的 SoftPilot.exe 不存在，已保留源文件。");
        }

        var sourceHash = await ComputeHashAsync(sourceExecutable, cancellationToken);
        var targetHash = await ComputeHashAsync(targetExecutable, cancellationToken);
        if (!string.Equals(sourceHash, targetHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new SoftPilotException("源应用本体已变化，已停止自动清理以保护文件。");
        }

        File.Delete(sourceExecutable);
    }

    public static bool PathsEqual(string left, string right) => string.Equals(
        NormalizeFile(left),
        NormalizeFile(right),
        StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ComputeHashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private static async Task CopyFileAsync(
        string source,
        string destination,
        CancellationToken cancellationToken)
    {
        await using var input = new FileStream(
            source,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output, cancellationToken);
    }

    private static void DeleteIfPresent(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string NormalizeFile(string path) => Path.GetFullPath(path);

}
