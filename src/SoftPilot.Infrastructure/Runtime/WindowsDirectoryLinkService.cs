using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Runtime;

public sealed class WindowsDirectoryLinkService
{
    private readonly ProcessRunner _processRunner;

    public WindowsDirectoryLinkService(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public async Task ReplaceAsync(string linkPath, string targetPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(targetPath))
        {
            throw new DirectoryNotFoundException(targetPath);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
        var temporary = linkPath + $".new-{Guid.NewGuid():N}";
        var backup = linkPath + $".old-{Guid.NewGuid():N}";
        try
        {
            await CreateAsync(temporary, targetPath, cancellationToken);
            var hadExisting = Exists(linkPath);
            if (hadExisting)
            {
                Directory.Move(linkPath, backup);
            }

            try
            {
                Directory.Move(temporary, linkPath);
                if (hadExisting)
                {
                    DeleteLink(backup);
                }
            }
            catch
            {
                if (hadExisting && !Exists(linkPath) && Exists(backup))
                {
                    Directory.Move(backup, linkPath);
                }

                throw;
            }
        }
        finally
        {
            if (Exists(temporary))
            {
                DeleteLink(temporary);
            }
        }
    }

    public void Delete(string linkPath)
    {
        if (Exists(linkPath))
        {
            DeleteLink(linkPath);
        }
    }

    private async Task CreateAsync(string linkPath, string targetPath, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }
        catch (UnauthorizedAccessException)
        {
            // Directory junctions do not require Developer Mode and are sufficient on a single NTFS volume.
        }
        catch (IOException)
        {
            // Fall back to a junction when symbolic-link creation is unavailable for this user.
        }

        var result = await _processRunner.RunAsync(
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            ["/d", "/c", "mklink", "/J", linkPath, targetPath],
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0 || !Exists(linkPath))
        {
            throw new SoftPilotException($"无法创建当前版本链接：{result.CombinedOutput}");
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void DeleteLink(string path) => Directory.Delete(path, recursive: false);
}
