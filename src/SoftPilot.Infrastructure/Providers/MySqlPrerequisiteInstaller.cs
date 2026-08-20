using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed class MySqlPrerequisiteInstaller
{
    internal static readonly Version MinimumVersion = new(14, 29, 30157);
    internal static readonly Uri DownloadUri = new("https://aka.ms/vc14/vc_redist.x64.exe");
    private readonly IDownloadService _downloads;
    private readonly IInstallationLayout _layout;
    private readonly IMySqlPrerequisiteSystem _system;

    public MySqlPrerequisiteInstaller(
        IDownloadService downloads,
        IInstallationLayout layout,
        ProcessRunner processRunner)
        : this(downloads, layout, new WindowsMySqlPrerequisiteSystem(processRunner))
    {
    }

    internal MySqlPrerequisiteInstaller(
        IDownloadService downloads,
        IInstallationLayout layout,
        IMySqlPrerequisiteSystem system)
    {
        _downloads = downloads;
        _layout = layout;
        _system = system;
    }

    public async Task EnsureInstalledAsync(
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new OperationProgress("prerequisite-check", null, "正在检查 Microsoft Visual C++ x64 Runtime"));
        if (IsCompatible(_system.GetInstalledVersion()))
        {
            return;
        }

        var installerPath = Path.Combine(_layout.DownloadsDirectory, "vc_redist.x64.exe");
        progress?.Report(new OperationProgress(
            "prerequisite-download",
            null,
            "正在从 Microsoft 下载最新受支持的 Visual C++ x64 Runtime"));
        await _downloads.DownloadAsync(
            DownloadUri,
            installerPath,
            progress: progress,
            cancellationToken: cancellationToken);

        progress?.Report(new OperationProgress("prerequisite-verify", null, "正在验证 Microsoft Authenticode 签名"));
        var signature = await _system.VerifyAuthenticodeAsync(installerPath, cancellationToken);
        if (!signature.IsValid || !IsMicrosoftPublisher(signature.Subject))
        {
            throw new IntegrityException(
                $"Visual C++ Runtime 安装程序的 Authenticode 签名无效或发布者不是 Microsoft（{signature.Subject ?? "无签名"}）。");
        }

        progress?.Report(new OperationProgress(
            "prerequisite-install",
            null,
            "正在安装 Microsoft Visual C++ x64 Runtime；Windows 可能显示管理员授权提示"));
        var result = await _system.InstallAsync(installerPath, cancellationToken);
        var installedVersion = _system.GetInstalledVersion();
        if (!IsCompatible(installedVersion))
        {
            throw new SoftPilotException(
                $"Microsoft Visual C++ x64 Runtime 安装后仍未检测到所需版本 {MinimumVersion} 或更高版本（退出码 {result.ExitCode}）。");
        }

        if (result.RestartRequired)
        {
            throw new SoftPilotException(
                "Microsoft Visual C++ x64 Runtime 已安装，但 Windows 要求重启。请重启后重新执行 MySQL 安装；已下载文件会保留在缓存中。");
        }
    }

    public Version? GetInstalledVersion() => _system.GetInstalledVersion();

    public bool IsInstalled() => IsCompatible(GetInstalledVersion());

    internal static bool IsCompatible(Version? version) => version is not null && version >= MinimumVersion;

    internal static bool IsMicrosoftPublisher(string? subject) =>
        subject?.Contains("CN=Microsoft Corporation", StringComparison.OrdinalIgnoreCase) == true
        && subject.Contains("O=Microsoft Corporation", StringComparison.OrdinalIgnoreCase);
}

internal interface IMySqlPrerequisiteSystem
{
    Version? GetInstalledVersion();
    Task<MySqlAuthenticodeVerification> VerifyAuthenticodeAsync(string path, CancellationToken cancellationToken);
    Task<MySqlPrerequisiteInstallResult> InstallAsync(string path, CancellationToken cancellationToken);
}

internal sealed record MySqlAuthenticodeVerification(bool IsValid, string? Subject);

internal sealed record MySqlPrerequisiteInstallResult(int ExitCode, bool RestartRequired);

internal sealed class WindowsMySqlPrerequisiteSystem(ProcessRunner processRunner) : IMySqlPrerequisiteSystem
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public Version? GetInstalledVersion()
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var runtime = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64");
        if (runtime?.GetValue("Installed") is not int installed || installed != 1)
        {
            return null;
        }

        var rawVersion = runtime.GetValue("Version") as string;
        if (Version.TryParse(rawVersion?.TrimStart('v', 'V'), out var version))
        {
            return version;
        }

        return TryReadVersionParts(runtime, out version) ? version : null;
    }

    public async Task<MySqlAuthenticodeVerification> VerifyAuthenticodeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $path = [Environment]::GetEnvironmentVariable('SOFTPILOT_VCREDIST_VERIFY_PATH')
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            [pscustomobject]@{
                IsValid = $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid
                Subject = $signature.SignerCertificate.Subject
            } | ConvertTo-Json -Compress
            """;
        var result = await processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            environment: new Dictionary<string, string?> { ["SOFTPILOT_VCREDIST_VERIFY_PATH"] = path },
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            return new MySqlAuthenticodeVerification(false, null);
        }

        return JsonSerializer.Deserialize<MySqlAuthenticodeVerification>(result.StandardOutput, JsonOptions)
            ?? new MySqlAuthenticodeVerification(false, null);
    }

    public async Task<MySqlPrerequisiteInstallResult> InstallAsync(
        string path,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
                Verb = "runas",
                Arguments = "/install /quiet /norestart",
            },
        };
        try
        {
            if (!process.Start())
            {
                throw new SoftPilotException("无法启动 Microsoft Visual C++ Runtime 安装程序。");
            }
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            throw new SoftPilotException("用户取消了 Microsoft Visual C++ Runtime 的管理员授权，MySQL 安装已停止。", exception);
        }

        // Once the elevated system installer starts, interrupting it could leave Windows Installer state inconsistent.
        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        var restartRequired = process.ExitCode is 1641 or 3010;
        if (process.ExitCode != 0 && !restartRequired)
        {
            throw new SoftPilotException($"Microsoft Visual C++ Runtime 安装失败，退出码 {process.ExitCode}。");
        }

        return new MySqlPrerequisiteInstallResult(process.ExitCode, restartRequired);
    }

    private static bool TryReadVersionParts(RegistryKey runtime, out Version? version)
    {
        version = null;
        if (runtime.GetValue("Major") is not int major
            || runtime.GetValue("Minor") is not int minor
            || runtime.GetValue("Bld") is not int build)
        {
            return false;
        }

        var revision = runtime.GetValue("Rbld") is int value ? value : 0;
        version = new Version(major, minor, build, revision);
        return true;
    }
}
