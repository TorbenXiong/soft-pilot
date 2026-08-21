using System.Text;

namespace SoftPilot.Infrastructure.Tools;

public sealed class WindowsHostsFileService : IHostsFileService
{
    private const int MaximumHostsLength = 1024 * 1024;
    private const int MaximumBackups = 20;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _backupDirectory;

    public WindowsHostsFileService(IInstallationLayout layout)
        : this(
            layout,
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "drivers",
                "etc",
                "hosts"))
    {
    }

    internal WindowsHostsFileService(IInstallationLayout layout, string hostsPath)
    {
        HostsPath = Path.GetFullPath(hostsPath);
        _backupDirectory = Path.Combine(layout.DataDirectory, "toolbox", "hosts-backups");
    }

    public string HostsPath { get; }

    public async Task<string> ReadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(HostsPath))
            {
                return string.Empty;
            }

            var content = await File.ReadAllBytesAsync(HostsPath, cancellationToken);
            return Decode(content);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new SoftPilotException("当前 Windows 用户无权读取 Hosts 文件。", exception);
        }
        catch (IOException exception)
        {
            throw new SoftPilotException("无法读取 Hosts 文件。", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SoftPilotException("Hosts 文件不是受支持的 UTF-8 或 Unicode 编码，已停止编辑以避免损坏内容。", exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(string content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length > MaximumHostsLength)
        {
            throw new SoftPilotException("Hosts 文件不能超过 1 MB。");
        }

        if (content.Contains('\0'))
        {
            throw new SoftPilotException("Hosts 文件不能包含空字符。");
        }

        await _gate.WaitAsync(cancellationToken);
        var temporaryPath = HostsPath + $".softpilot.{Guid.NewGuid():N}.tmp";
        try
        {
            var currentBytes = File.Exists(HostsPath)
                ? await File.ReadAllBytesAsync(HostsPath, cancellationToken)
                : [];
            _ = Decode(currentBytes);
            await BackupAsync(currentBytes, cancellationToken);

            var encoding = DetectEncoding(currentBytes);
            await File.WriteAllTextAsync(temporaryPath, content, encoding, cancellationToken);
            if (File.Exists(HostsPath))
            {
                File.Replace(temporaryPath, HostsPath, null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, HostsPath);
            }
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new AdministratorPrivilegesRequiredException(
                "保存 Hosts 需要管理员权限。",
                exception);
        }
        catch (IOException exception)
        {
            throw new SoftPilotException("无法安全保存 Hosts 文件。", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new SoftPilotException("Hosts 文件不是受支持的 UTF-8 或 Unicode 编码，已停止保存以避免损坏内容。", exception);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }

            _gate.Release();
        }
    }

    private async Task BackupAsync(byte[] content, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_backupDirectory);
        var backupPath = Path.Combine(
            _backupDirectory,
            $"hosts-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.bak");
        await File.WriteAllBytesAsync(backupPath, content, cancellationToken);

        foreach (var obsolete in Directory.EnumerateFiles(_backupDirectory, "hosts-*.bak")
                     .OrderByDescending(File.GetCreationTimeUtc)
                     .Skip(MaximumBackups))
        {
            File.Delete(obsolete);
        }
    }

    private static Encoding DetectEncoding(byte[] content)
    {
        if (content.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: true, throwOnInvalidBytes: true);
        }

        if (content.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
        }

        if (content.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
        {
            return new UnicodeEncoding(bigEndian: true, byteOrderMark: true, throwOnInvalidBytes: true);
        }

        return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    }

    private static string Decode(byte[] content)
    {
        var encoding = DetectEncoding(content);
        var preamble = encoding.GetPreamble();
        var offset = content.AsSpan().StartsWith(preamble) ? preamble.Length : 0;
        return encoding.GetString(content, offset, content.Length - offset);
    }
}
