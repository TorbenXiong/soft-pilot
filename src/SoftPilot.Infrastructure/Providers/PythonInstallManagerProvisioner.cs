using System.Text.Json;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed class PythonInstallManagerProvisioner
{
    private const long MaximumPackageSize = 128L * 1024 * 1024;
    private const string ExpectedPublisher =
        "CN=Python Software Foundation, O=Python Software Foundation, L=Beaverton, S=Oregon, C=US";
    private static readonly Uri AppInstallerUri =
        new("https://www.python.org/ftp/python/pymanager/pymanager.appinstaller");
    private static readonly HashSet<string> OfficialPackageFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "PythonSoftwareFoundation.PythonManager_3847v3x7pw1km",
        "PythonSoftwareFoundation.PythonManager_qbz5n2kfra8p0",
    };

    private readonly IDownloadService _downloads;
    private readonly IInstallationLayout _layout;
    private readonly IPythonInstallManagerSystem _system;
    private readonly string _lockPath;

    public PythonInstallManagerProvisioner(
        IDownloadService downloads,
        IInstallationLayout layout,
        ProcessRunner processRunner)
        : this(downloads, layout, new PowerShellPythonInstallManagerSystem(processRunner))
    {
    }

    internal PythonInstallManagerProvisioner(
        IDownloadService downloads,
        IInstallationLayout layout,
        IPythonInstallManagerSystem system)
    {
        _downloads = downloads;
        _layout = layout;
        _system = system;
        _lockPath = Path.Combine(layout.DataDirectory, "python-manager-provision.lock");
    }

    internal async ValueTask<PythonInstallManagerLease> AcquireAsync(
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var processLock = await AcquireProcessLockAsync(cancellationToken);
        try
        {
            var packages = await _system.FindPackagesAsync(cancellationToken);
            var existingPackage = packages.FirstOrDefault(package =>
                OfficialPackageFamilies.Contains(package.PackageFamilyName));
            if (existingPackage is not null)
            {
                var executable = await FindPackageExecutableAsync(existingPackage, cancellationToken)
                    ?? throw new SoftPilotException(
                        "检测到 Python Install Manager，但 pymanager.exe 应用执行别名不可用。请在 Windows 的‘应用执行别名’中启用它。");
                return new PythonInstallManagerLease(executable, processLock, cleanup: null);
            }

            if (packages.Count > 0)
            {
                throw new IntegrityException("检测到身份无法确认的 Python Install Manager 包，已拒绝使用。");
            }

            var pathExecutable = _system.FindExecutableOnPath();
            if (pathExecutable is not null)
            {
                var signature = await _system.VerifyAuthenticodeAsync(pathExecutable, cancellationToken);
                if (!signature.IsValid
                    || !string.Equals(signature.Subject, ExpectedPublisher, StringComparison.Ordinal))
                {
                    throw new IntegrityException("PATH 中的 pymanager.exe 没有有效的 Python Software Foundation 签名。");
                }

                return new PythonInstallManagerLease(pathExecutable, processLock, cleanup: null);
            }

            progress?.Report(new OperationProgress("manager", 10, "正在准备 Python Install Manager"));
            var cacheDirectory = Path.Combine(_layout.DownloadsDirectory, "python-manager");
            Directory.CreateDirectory(cacheDirectory);
            var appInstallerPath = Path.Combine(cacheDirectory, "pymanager.appinstaller");
            await _downloads.DownloadAsync(
                AppInstallerUri,
                appInstallerPath,
                progress: MapDownloadProgress(progress, 10, 12, "正在下载 Python Install Manager 元数据"),
                cancellationToken: cancellationToken);
            var manifest = PythonInstallManagerManifest.ParseAppInstaller(appInstallerPath);
            var packagePath = Path.Combine(cacheDirectory, Path.GetFileName(manifest.PackageUri.AbsolutePath));
            if (!await IsCachedPackageValidAsync(manifest, packagePath, cancellationToken))
            {
                if (File.Exists(packagePath))
                {
                    File.Delete(packagePath);
                }

                await _downloads.DownloadAsync(
                    manifest.PackageUri,
                    packagePath,
                    progress: MapDownloadProgress(progress, 12, 20, "正在下载 Python Install Manager"),
                    cancellationToken: cancellationToken);
            }

            await ValidatePackageAsync(manifest, packagePath, cancellationToken);
            progress?.Report(new OperationProgress("manager", 21, "正在临时安装 Python Install Manager"));
            PythonInstallManagerPackage installedPackage;
            string managerExecutable;
            try
            {
                await _system.InstallPackageAsync(packagePath, cancellationToken);
                installedPackage = await FindInstalledPackageAsync(manifest, cancellationToken);
                managerExecutable = await FindPackageExecutableAsync(installedPackage, cancellationToken)
                    ?? throw new SoftPilotException(
                        "临时安装 Python Install Manager 后未找到 pymanager.exe。");
            }
            catch
            {
                await RemoveNewPackageIfPresentAsync(manifest, CancellationToken.None);
                throw;
            }

            return new PythonInstallManagerLease(
                managerExecutable,
                processLock,
                () => RemovePackageAsync(installedPackage, progress, CancellationToken.None));
        }
        catch
        {
            await processLock.DisposeAsync();
            throw;
        }
    }

    private async Task<bool> IsCachedPackageValidAsync(
        PythonInstallManagerManifest manifest,
        string packagePath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(packagePath))
        {
            return false;
        }

        try
        {
            await ValidatePackageAsync(manifest, packagePath, cancellationToken);
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or IntegrityException)
        {
            return false;
        }
    }

    private async Task ValidatePackageAsync(
        PythonInstallManagerManifest manifest,
        string packagePath,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(packagePath);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumPackageSize)
        {
            throw new IntegrityException("Python Install Manager MSIX 文件大小无效。");
        }

        manifest.ValidatePackageArchive(packagePath);
        var signature = await _system.VerifyAuthenticodeAsync(packagePath, cancellationToken);
        if (!signature.IsValid
            || !string.Equals(signature.Subject, manifest.Publisher, StringComparison.Ordinal))
        {
            throw new IntegrityException("Python Install Manager MSIX 的 Authenticode 签名无效。");
        }
    }

    private async Task<PythonInstallManagerPackage> FindInstalledPackageAsync(
        PythonInstallManagerManifest manifest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var package = await FindPackageAsync(manifest, cancellationToken);
            if (package is not null)
            {
                return package;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new SoftPilotException("Windows 未能完成 Python Install Manager 的临时注册。");
    }

    private async Task<string?> FindPackageExecutableAsync(
        PythonInstallManagerPackage package,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var executable = _system.FindPackageExecutable(package);
            if (executable is not null)
            {
                return executable;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        return null;
    }

    private async Task RemoveNewPackageIfPresentAsync(
        PythonInstallManagerManifest manifest,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var package = await FindPackageAsync(manifest, cancellationToken);
            if (package is not null)
            {
                await RemovePackageAsync(package, progress: null, cancellationToken);
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    private async Task<PythonInstallManagerPackage?> FindPackageAsync(
        PythonInstallManagerManifest manifest,
        CancellationToken cancellationToken) =>
        (await _system.FindPackagesAsync(cancellationToken)).FirstOrDefault(item =>
            string.Equals(item.Name, manifest.PackageName, StringComparison.Ordinal)
            && string.Equals(item.Publisher, manifest.Publisher, StringComparison.Ordinal)
            && item.Version == manifest.Version
            && OfficialPackageFamilies.Contains(item.PackageFamilyName));

    private async ValueTask RemovePackageAsync(
        PythonInstallManagerPackage package,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress("manager", 79, "正在移除临时 Python Install Manager"));
        await _system.RemovePackageAsync(package.PackageFullName, cancellationToken);
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var remains = (await _system.FindPackagesAsync(cancellationToken)).Any(item =>
                string.Equals(item.PackageFullName, package.PackageFullName, StringComparison.OrdinalIgnoreCase));
            if (!remains && !_system.PackageFamilyAliasExists(package))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new SoftPilotException("Python Install Manager 临时包未能完整卸载。");
    }

    private async ValueTask<IAsyncDisposable> AcquireProcessLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_lockPath)!);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new AsyncFileLease(new FileStream(
                    _lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    1,
                    FileOptions.Asynchronous));
            }
            catch (IOException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
        }
    }

    private static IProgress<OperationProgress>? MapDownloadProgress(
        IProgress<OperationProgress>? progress,
        double start,
        double end,
        string detail) =>
        progress is null
            ? null
            : new MappedProgress(progress, start, end, detail);

    private sealed class MappedProgress(
        IProgress<OperationProgress> target,
        double start,
        double end,
        string detail) : IProgress<OperationProgress>
    {
        public void Report(OperationProgress value) => target.Report(new OperationProgress(
            "manager",
            value.Percentage is null
                ? start
                : start + (end - start) * value.Percentage.Value / 100,
            detail));
    }

    private sealed class AsyncFileLease(FileStream stream) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            stream.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class PythonInstallManagerLease : IAsyncDisposable
{
    private IAsyncDisposable? _processLock;
    private Func<ValueTask>? _cleanup;

    public PythonInstallManagerLease(
        string executablePath,
        IAsyncDisposable processLock,
        Func<ValueTask>? cleanup)
    {
        ExecutablePath = executablePath;
        _processLock = processLock;
        _cleanup = cleanup;
    }

    public string ExecutablePath { get; }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_cleanup is not null)
            {
                await _cleanup();
            }
        }
        finally
        {
            _cleanup = null;
            if (_processLock is not null)
            {
                await _processLock.DisposeAsync();
                _processLock = null;
            }
        }
    }
}

internal interface IPythonInstallManagerSystem
{
    Task<IReadOnlyList<PythonInstallManagerPackage>> FindPackagesAsync(CancellationToken cancellationToken);
    string? FindPackageExecutable(PythonInstallManagerPackage package);
    bool PackageFamilyAliasExists(PythonInstallManagerPackage package);
    string? FindExecutableOnPath();
    Task<AuthenticodeVerification> VerifyAuthenticodeAsync(string path, CancellationToken cancellationToken);
    Task InstallPackageAsync(string path, CancellationToken cancellationToken);
    Task RemovePackageAsync(string packageFullName, CancellationToken cancellationToken);
}

internal sealed record PythonInstallManagerPackage(
    string Name,
    string PackageFullName,
    string PackageFamilyName,
    string Publisher,
    Version Version);

internal sealed record AuthenticodeVerification(bool IsValid, string? Subject);

internal sealed class PowerShellPythonInstallManagerSystem(ProcessRunner processRunner)
    : IPythonInstallManagerSystem
{
    private const string PackageName = "PythonSoftwareFoundation.PythonManager";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<IReadOnlyList<PythonInstallManagerPackage>> FindPackagesAsync(
        CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $items = @(Get-AppxPackage -Name 'PythonSoftwareFoundation.PythonManager' | ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    PackageFullName = $_.PackageFullName
                    PackageFamilyName = $_.PackageFamilyName
                    Publisher = $_.Publisher
                    Version = [string]$_.Version
                }
            })
            ConvertTo-Json -InputObject $items -Compress
            """;
        var result = await RunPowerShellAsync(script, environment: null, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new SoftPilotException($"无法查询 Python Install Manager：{result.CombinedOutput}");
        }

        using var document = JsonDocument.Parse(result.StandardOutput);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToArray()
            : [document.RootElement];
        return elements.Select(element => new PythonInstallManagerPackage(
                element.GetProperty("Name").GetString() ?? string.Empty,
                element.GetProperty("PackageFullName").GetString() ?? string.Empty,
                element.GetProperty("PackageFamilyName").GetString() ?? string.Empty,
                element.GetProperty("Publisher").GetString() ?? string.Empty,
                Version.Parse(element.GetProperty("Version").GetString() ?? "0.0")))
            .Where(package => string.Equals(package.Name, PackageName, StringComparison.Ordinal))
            .ToArray();
    }

    public string? FindPackageExecutable(PythonInstallManagerPackage package)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        var familyAlias = Path.Combine(windowsApps, package.PackageFamilyName, "pymanager.exe");
        if (File.Exists(familyAlias))
        {
            return familyAlias;
        }

        var rootAlias = Path.Combine(windowsApps, "pymanager.exe");
        return File.Exists(rootAlias) ? rootAlias : null;
    }

    public bool PackageFamilyAliasExists(PythonInstallManagerPackage package)
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var alias = Path.Combine(
            localAppData,
            "Microsoft",
            "WindowsApps",
            package.PackageFamilyName,
            "pymanager.exe");
        return File.Exists(alias);
    }

    public string? FindExecutableOnPath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsApps = Path.GetFullPath(Path.Combine(localAppData, "Microsoft", "WindowsApps"));
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };
        foreach (var directory in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .SelectMany(path => path!.Split(
                         Path.PathSeparator,
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(directory.Trim('"'), "pymanager.exe");
            var fullCandidate = Path.GetFullPath(candidate);
            if (File.Exists(fullCandidate)
                && !fullCandidate.StartsWith(
                    windowsApps + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))
            {
                return fullCandidate;
            }
        }

        return null;
    }

    public async Task<AuthenticodeVerification> VerifyAuthenticodeAsync(
        string path,
        CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $path = [Environment]::GetEnvironmentVariable('SOFTPILOT_PYMANAGER_VERIFY_PATH')
            $signature = Get-AuthenticodeSignature -LiteralPath $path
            [pscustomobject]@{
                IsValid = $signature.Status -eq [System.Management.Automation.SignatureStatus]::Valid
                Subject = $signature.SignerCertificate.Subject
            } | ConvertTo-Json -Compress
            """;
        var environment = new Dictionary<string, string?>
        {
            ["SOFTPILOT_PYMANAGER_VERIFY_PATH"] = path,
        };
        var result = await RunPowerShellAsync(script, environment, cancellationToken);
        if (result.ExitCode != 0)
        {
            return new AuthenticodeVerification(false, null);
        }

        return JsonSerializer.Deserialize<AuthenticodeVerification>(result.StandardOutput, JsonOptions)
            ?? new AuthenticodeVerification(false, null);
    }

    public async Task InstallPackageAsync(string path, CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $path = [Environment]::GetEnvironmentVariable('SOFTPILOT_PYMANAGER_PACKAGE_PATH')
            Add-AppxPackage -Path $path -ErrorAction Stop
            """;
        var environment = new Dictionary<string, string?>
        {
            ["SOFTPILOT_PYMANAGER_PACKAGE_PATH"] = path,
        };
        var result = await RunPowerShellAsync(script, environment, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new SoftPilotException($"临时安装 Python Install Manager 失败：{result.CombinedOutput}");
        }
    }

    public async Task RemovePackageAsync(string packageFullName, CancellationToken cancellationToken)
    {
        const string script = """
            $ErrorActionPreference = 'Stop'
            $package = [Environment]::GetEnvironmentVariable('SOFTPILOT_PYMANAGER_PACKAGE_FULL_NAME')
            Remove-AppxPackage -Package $package -ErrorAction Stop
            """;
        var environment = new Dictionary<string, string?>
        {
            ["SOFTPILOT_PYMANAGER_PACKAGE_FULL_NAME"] = packageFullName,
        };
        var result = await RunPowerShellAsync(script, environment, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new SoftPilotException($"卸载临时 Python Install Manager 失败：{result.CombinedOutput}");
        }
    }

    private Task<ProcessResult> RunPowerShellAsync(
        string script,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken) =>
        processRunner.RunAsync(
            "powershell.exe",
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script],
            environment: environment,
            cancellationToken: cancellationToken);
}
