using System.Text.Json;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;

namespace SoftPilot.Infrastructure.Providers;

public sealed class PythonRuntimeProvider : IRuntimeProvider
{
    private static readonly Uri OfficialIndexUri = new("https://www.python.org/ftp/python/index-windows.json");
    private static readonly Regex StableVersionPattern = new("^\\d+\\.\\d+\\.\\d+$", RegexOptions.CultureInvariant);
    private static readonly Regex StandardX64TagPattern = new("^\\d+\\.\\d+-64$", RegexOptions.CultureInvariant);
    private readonly ProcessRunner _processRunner;

    public PythonRuntimeProvider(ProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public RuntimeKind Kind => RuntimeKind.Python;

    public async Task<IReadOnlyList<RuntimeRelease>> GetAvailableAsync(CancellationToken cancellationToken = default)
    {
        var manager = FindManager();
        var result = await _processRunner.RunAsync(
            manager,
            ["list", $"--source={OfficialIndexUri}", "--format=json"],
            environment: SafeManagerEnvironment(),
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new SoftPilotException($"Python Install Manager 查询失败：{result.CombinedOutput}");
        }

        return ParseReleases(result.StandardOutput);
    }

    internal static IReadOnlyList<RuntimeRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var versions = root.ValueKind == JsonValueKind.Array
            ? root
            : root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("versions", out var versionsElement)
                && versionsElement.ValueKind == JsonValueKind.Array
                    ? versionsElement
                    : throw new JsonException("Python Install Manager 返回的 JSON 中缺少 versions 数组。");

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

            releases.Add(new RuntimeRelease(
                RuntimeKind.Python,
                version,
                RuntimeArchitecture.X64,
                OfficialIndexUri,
                null));
        }

        return releases
            .DistinctBy(release => release.Version)
            .OrderByDescending(release => release.Version, RuntimeVersionComparer.Instance)
            .ToArray();
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
        progress?.Report(new OperationProgress("install", null, "Python Install Manager 正在验证并提取官方运行时"));
        Directory.CreateDirectory(stagingDirectory);
        var result = await _processRunner.RunAsync(
            FindManager(),
            ["install", $"--source={OfficialIndexUri}", $"--target={stagingDirectory}", $"{release.Version}-64"],
            environment: SafeManagerEnvironment(),
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new SoftPilotException($"Python Install Manager 安装失败：{result.CombinedOutput}");
        }
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

    private static IReadOnlyDictionary<string, string?> SafeManagerEnvironment() =>
        new Dictionary<string, string?>
        {
            ["PYTHON_MANAGER_AUTOMATIC_INSTALL"] = "false",
            ["PYTHON_MANAGER_CONFIRM"] = "false",
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
