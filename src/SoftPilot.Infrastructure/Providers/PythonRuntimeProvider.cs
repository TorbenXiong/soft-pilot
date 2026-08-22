using System.Text.Json;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed class PythonRuntimeProvider : IRuntimeProvider
{
    private static readonly Uri OfficialIndexUri = new("https://www.python.org/ftp/python/index-windows.json");
    private static readonly Uri OfficialDownloadDirectory = new("https://www.python.org/ftp/python/");
    private const int MaxCatalogPages = 8;
    private static readonly Regex StableVersionPattern = new("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex StandardX64TagPattern = new("^\\d+\\.\\d+-64$", RegexOptions.CultureInvariant);
    private readonly HttpClient _client;
    private readonly ProcessRunner _processRunner;
    private readonly PythonInstallManagerProvisioner? _managerProvisioner;
    private readonly IInstallationLayout? _layout;

    public PythonRuntimeProvider(HttpClient client, ProcessRunner processRunner)
    {
        _client = client;
        _processRunner = processRunner;
    }

    public PythonRuntimeProvider(
        HttpClient client,
        ProcessRunner processRunner,
        PythonInstallManagerProvisioner managerProvisioner,
        IInstallationLayout layout)
        : this(client, processRunner)
    {
        _managerProvisioner = managerProvisioner;
        _layout = layout;
    }

    public RuntimeKind Kind => RuntimeKind.Python;

    public async Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        var releases = new List<RuntimeRelease>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Uri? pageUri = OfficialIndexUri;
        for (var page = 0; page < MaxCatalogPages && pageUri is not null; page++)
        {
            if (!visited.Add(pageUri.AbsoluteUri))
            {
                throw new IntegrityException($"Python 官方版本目录包含循环分页：{pageUri}");
            }

            var json = await ProviderUtilities.GetRequiredStringAsync(_client, pageUri, cancellationToken);
            releases.AddRange(ParseReleases(
                json,
                skipUnsupportedPackages: IsLegacyIndexPage(pageUri)));
            pageUri = ParseNextIndexUri(json, pageUri);
        }

        if (pageUri is not null)
        {
            throw new IntegrityException($"Python 官方版本目录分页超过安全上限 {MaxCatalogPages}。");
        }

        var result = releases
            .DistinctBy(release => release.Version)
            .OrderByDescending(release => release.Version, RuntimeVersionComparer.Instance)
            .ToArray();
        return result.Length > 0
            ? result
            : throw new IntegrityException("Python 官方版本目录中没有可验证的 Windows x64 稳定版本。");
    }

    internal static IReadOnlyList<RuntimeRelease> ParseReleases(
        string json,
        bool skipUnsupportedPackages = false)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var versions = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("versions", out var versionsElement)
                && versionsElement.ValueKind == JsonValueKind.Array
                    ? versionsElement
                    : throw new JsonException("Python 官方版本目录中缺少 versions 数组。");

        var releases = new List<RuntimeRelease>();
        foreach (var item in versions.EnumerateArray())
        {
            var company = ProviderUtilities.ReadFlexibleString(item, "company", "Company");
            if (!string.Equals(company, "PythonCore", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var version = ProviderUtilities.ReadFlexibleString(item, "sort-version", "sort_version", "version", "Version");
            var tag = ProviderUtilities.ReadFlexibleString(item, "tag", "Tag");
            if (string.IsNullOrWhiteSpace(version)
                || string.IsNullOrWhiteSpace(tag)
                || !StableVersionPattern.IsMatch(version)
                || !StandardX64TagPattern.IsMatch(tag))
            {
                continue;
            }

            Uri downloadUri;
            string sha256;
            try
            {
                downloadUri = ParseOfficialDownloadUri(item, version);
                sha256 = ParseSha256(item, version);
            }
            catch (IntegrityException) when (skipUnsupportedPackages)
            {
                // The official legacy page contains historical NuGet packages without
                // the python.org URL and SHA-256 guarantees required by SoftPilot.
                continue;
            }

            releases.Add(new RuntimeRelease(
                RuntimeKind.Python,
                version,
                RuntimeArchitecture.X64,
                downloadUri,
                sha256,
                ReleasePageUri: new Uri(
                    $"https://www.python.org/downloads/release/python-{version.Replace(".", string.Empty, StringComparison.Ordinal)}/")));
        }

        return releases
            .DistinctBy(release => release.Version)
            .OrderByDescending(release => release.Version, RuntimeVersionComparer.Instance)
            .ToArray();
    }

    private static bool IsLegacyIndexPage(Uri pageUri) =>
        pageUri.AbsolutePath.EndsWith("/index-windows-legacy.json", StringComparison.Ordinal);

    private static Uri ParseOfficialDownloadUri(JsonElement item, string version)
    {
        var value = ProviderUtilities.ReadFlexibleString(item, "url", "Url");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(uri.Host, OfficialDownloadDirectory.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != OfficialDownloadDirectory.Port
            || !uri.AbsolutePath.StartsWith(OfficialDownloadDirectory.AbsolutePath, StringComparison.Ordinal))
        {
            throw new IntegrityException($"Python {version} 的官方运行时下载地址无效。");
        }

        return uri;
    }

    private static string ParseSha256(JsonElement item, string version)
    {
        if (!item.TryGetProperty("hash", out var hash)
            || hash.ValueKind != JsonValueKind.Object
            || !hash.TryGetProperty("sha256", out var sha256Element)
            || sha256Element.ValueKind != JsonValueKind.String)
        {
            throw new IntegrityException($"Python {version} 的官方版本目录缺少 SHA-256。");
        }

        var sha256 = sha256Element.GetString();
        if (sha256 is null
            || sha256.Length != 64
            || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            throw new IntegrityException($"Python {version} 的官方 SHA-256 格式无效。");
        }

        return sha256;
    }

    internal static Uri? ParseNextIndexUri(string json, Uri currentPageUri)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("next", out var nextElement)
            || nextElement.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (nextElement.ValueKind != JsonValueKind.String)
        {
            throw new JsonException("Python 官方版本目录的 next 字段格式无效。");
        }

        var next = nextElement.GetString();
        if (string.IsNullOrWhiteSpace(next))
        {
            return null;
        }

        var nextUri = new Uri(currentPageUri, next);
        var officialDirectory = new Uri(OfficialIndexUri, ".");
        if (nextUri.Scheme != Uri.UriSchemeHttps
            || !string.Equals(nextUri.Host, OfficialIndexUri.Host, StringComparison.OrdinalIgnoreCase)
            || nextUri.Port != OfficialIndexUri.Port
            || !nextUri.AbsolutePath.StartsWith(officialDirectory.AbsolutePath, StringComparison.Ordinal))
        {
            throw new IntegrityException($"拒绝读取非 python.org 官方目录的 Python 分页：{nextUri}");
        }

        return nextUri;
    }

    public async Task<RuntimeRelease> ResolveAsync(string exactVersion, CancellationToken cancellationToken = default)
    {
        var normalized = ProviderUtilities.NormalizeVersion(exactVersion);
        return (await GetAvailableAsync(cancellationToken))
            .FirstOrDefault(release => string.Equals(release.Version, normalized, StringComparison.OrdinalIgnoreCase))
            ?? throw new RuntimeNotFoundException(Kind, exactVersion);
    }

    public async Task PrepareAsync(
        RuntimeRelease release,
        string stagingDirectory,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(stagingDirectory);
        if (_managerProvisioner is null || _layout is null)
        {
            await RunManagerAsync(
                FindManager(),
                release,
                stagingDirectory,
                SafeManagerEnvironment(configPath: null, logsDirectory: null),
                progress,
                cancellationToken);
        }
        else
        {
            var configPath = await CreateManagerConfigAsync(_layout, release.Version, cancellationToken);
            try
            {
                await using (var manager = await _managerProvisioner.AcquireAsync(progress, cancellationToken))
                {
                    await RunManagerAsync(
                        manager.ExecutablePath,
                        release,
                        stagingDirectory,
                        SafeManagerEnvironment(configPath, _layout.LogsDirectory),
                        progress,
                        cancellationToken);
                }
            }
            finally
            {
                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                }
            }
        }

        progress?.Report(new OperationProgress("extract", 80, "Python 官方运行时已下载并提取"));
    }

    private async Task RunManagerAsync(
        string executable,
        RuntimeRelease release,
        string stagingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        IProgress<OperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress?.Report(new OperationProgress("download", 25, "Python Install Manager 正在下载并验证官方运行时"));
        var result = await _processRunner.RunAsync(
            executable,
            ["install", $"--source={OfficialIndexUri}", $"--target={stagingDirectory}", $"{release.Version}-64"],
            environment: environment,
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new SoftPilotException($"Python Install Manager 安装失败：{result.CombinedOutput}");
        }
    }

    private static async Task<string> CreateManagerConfigAsync(
        IInstallationLayout layout,
        string version,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(layout.StagingDirectory);
        Directory.CreateDirectory(layout.DownloadsDirectory);
        Directory.CreateDirectory(layout.LogsDirectory);
        var pythonDownloads = Path.Combine(layout.DownloadsDirectory, "python", version);
        var pythonLogs = Path.Combine(layout.LogsDirectory, "python", version);
        Directory.CreateDirectory(pythonDownloads);
        Directory.CreateDirectory(pythonLogs);
        var path = Path.Combine(layout.StagingDirectory, $"python-manager-{Guid.NewGuid():N}.json");
        var config = new Dictionary<string, object?>
        {
            ["automatic_install"] = false,
            ["confirm"] = false,
            ["download_dir"] = pythonDownloads,
            ["logs_dir"] = pythonLogs,
        };
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, config, cancellationToken: cancellationToken);
        await stream.FlushAsync(cancellationToken);
        return path;
    }

    public async Task<RuntimeHealth> CheckHealthAsync(string runtimeDirectory, CancellationToken cancellationToken = default)
    {
        var executable = Path.Combine(runtimeDirectory, "python.exe");
        if (!File.Exists(executable))
        {
            return new RuntimeHealth(false, null, "缺少 python.exe。");
        }

        var result = await _processRunner.RunAsync(
            executable,
            ["--version"],
            environment: new Dictionary<string, string?> { ["PYTHONHOME"] = null },
            cancellationToken: cancellationToken);
        var output = result.CombinedOutput;
        var versionLine = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(line => line.StartsWith("Python ", StringComparison.OrdinalIgnoreCase));
        var version = versionLine?[7..].Trim();
        if (result.ExitCode != 0 || version is null)
        {
            return new RuntimeHealth(false, null, output);
        }

        var pip = await _processRunner.RunAsync(
            executable,
            ["-m", "pip", "--version"],
            environment: IsolatedPythonEnvironment(),
            cancellationToken: cancellationToken);
        return pip.ExitCode == 0 && pip.CombinedOutput.StartsWith("pip ", StringComparison.OrdinalIgnoreCase)
            ? new RuntimeHealth(true, version)
            : new RuntimeHealth(false, null, $"pip 健康检查失败：{pip.CombinedOutput}");
    }

    private static string FindManager()
    {
        var command = FindOnPath("pymanager.exe") ?? FindAppAlias();
        return command ?? throw new SoftPilotException(
            "未找到官方 Python Install Manager。请先从 python.org 或 Microsoft Store 安装它；SoftPilot 不会静默安装系统组件。");
    }

    private static string? FindOnPath(string fileName)
    {
        var paths = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
        };
        foreach (var directory in paths
                     .Where(path => !string.IsNullOrWhiteSpace(path))
                     .SelectMany(path => path!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var candidate = Path.Combine(directory.Trim('"'), fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? FindAppAlias()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var windowsApps = Path.Combine(localAppData, "Microsoft", "WindowsApps");
        if (!Directory.Exists(windowsApps))
        {
            return null;
        }

        var rootAlias = Path.Combine(windowsApps, "pymanager.exe");
        if (File.Exists(rootAlias))
        {
            return rootAlias;
        }

        try
        {
            return Directory.EnumerateDirectories(windowsApps, "PythonSoftwareFoundation.PythonManager_*")
                .Select(directory => Path.Combine(directory, "pymanager.exe"))
                .FirstOrDefault(File.Exists);
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IReadOnlyDictionary<string, string?> SafeManagerEnvironment(
        string? configPath,
        string? logsDirectory) =>
        new Dictionary<string, string?>
        {
            ["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "false",
            ["PYTHON_MANAGER_CONFIRM"] = "false",
            ["PYTHON_MANAGER_CONFIG"] = configPath,
            ["PYTHON_MANAGER_LOGS"] = logsDirectory,
            ["PYTHON_MANAGER_SOURCE_URL"] = OfficialIndexUri.AbsoluteUri,
            ["PYTHONHOME"] = null,
            ["PYTHONPATH"] = null,
        };

    private static IReadOnlyDictionary<string, string?> IsolatedPythonEnvironment() =>
        new Dictionary<string, string?>
        {
            ["PYTHONHOME"] = null,
            ["PYTHONPATH"] = null,
        };
}
