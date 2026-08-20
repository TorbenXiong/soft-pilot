using Microsoft.Win32;
using System.Text.RegularExpressions;
using SoftPilot.Infrastructure.IO;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Infrastructure.Detection;

public sealed class WindowsExternalRuntimeDetector : IExternalRuntimeDetector
{
    private static readonly Regex StandardVersionPattern = new(
        "^\\d+\\.\\d+\\.\\d+(?:[-+][0-9A-Za-z.-]+)?$",
        RegexOptions.CultureInvariant);
    private static readonly Regex RedisVersionPattern = new(
        "(?:Redis server )?v=(\\d+\\.\\d+\\.\\d+)",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private readonly ProcessRunner _processRunner;
    private readonly string _managementDirectory;

    public WindowsExternalRuntimeDetector(
        RuntimeKind kind,
        ProcessRunner processRunner,
        IInstallationLayout layout)
    {
        Kind = kind;
        _processRunner = processRunner;
        _managementDirectory = layout.ManagementDirectory;
    }

    public RuntimeKind Kind { get; }

    public async Task<IReadOnlyList<ExternalRuntime>> DetectAsync(CancellationToken cancellationToken = default)
    {
        var candidates = GetCandidates()
            .Where(candidate => !IsPathUnderDirectory(candidate, _managementDirectory))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var result = new List<ExternalRuntime>();
        foreach (var executable in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var process = await _processRunner.RunAsync(
                    executable,
                    Kind == RuntimeKind.Java ? ["-version"] : ["--version"],
                    environment: Kind == RuntimeKind.Python
                        ? new Dictionary<string, string?> { ["PYTHONHOME"] = null }
                        : null,
                    cancellationToken: cancellationToken);
                var version = ParseVersion(Kind, process.CombinedOutput);
                if (process.ExitCode == 0 && version is not null)
                {
                    result.Add(new ExternalRuntime(
                        Kind,
                        version,
                        RuntimeArchitecture.X64,
                        executable,
                        GetSource(executable)));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or SoftPilotException)
            {
                // Discovery is best-effort; inaccessible external installations stay read-only and hidden.
            }
        }

        return result;
    }

    private IEnumerable<string> GetCandidates()
    {
        var fileName = Kind switch
        {
            RuntimeKind.Node => "node.exe",
            RuntimeKind.Java => "java.exe",
            RuntimeKind.Python => "python.exe",
            RuntimeKind.Redis => "redis-server.exe",
            _ => throw new ArgumentOutOfRangeException(),
        };

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Kind == RuntimeKind.Python && IsWindowsAppsDirectory(directory))
            {
                continue;
            }

            yield return Path.Combine(directory.Trim('"'), fileName);
        }

        if (Kind == RuntimeKind.Java)
        {
            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome))
            {
                yield return Path.Combine(javaHome, "bin", fileName);
            }

            foreach (var candidate in EnumerateUnderProgramFiles("Eclipse Adoptium", "bin\\java.exe"))
            {
                yield return candidate;
            }

            foreach (var candidate in EnumerateUnderProgramFiles("Java", "bin\\java.exe"))
            {
                yield return candidate;
            }
        }
        else if (Kind == RuntimeKind.Node)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            yield return Path.Combine(programFiles, "nodejs", fileName);
        }
        else if (Kind == RuntimeKind.Python)
        {
            foreach (var candidate in EnumeratePythonRegistry(RegistryHive.CurrentUser))
            {
                yield return candidate;
            }

            foreach (var candidate in EnumeratePythonRegistry(RegistryHive.LocalMachine))
            {
                yield return candidate;
            }
        }
    }

    private static IEnumerable<string> EnumerateUnderProgramFiles(string vendorDirectory, string suffix)
    {
        foreach (var specialFolder in new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var root = Path.Combine(Environment.GetFolderPath(specialFolder), vendorDirectory);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                yield return Path.Combine(directory, suffix);
            }
        }
    }

    private static IEnumerable<string> EnumeratePythonRegistry(RegistryHive hive)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var python = baseKey.OpenSubKey(@"Software\Python");
        if (python is null)
        {
            yield break;
        }

        foreach (var companyName in python.GetSubKeyNames())
        {
            using var company = python.OpenSubKey(companyName);
            if (company is null)
            {
                continue;
            }

            foreach (var tagName in company.GetSubKeyNames())
            {
                using var installPath = company.OpenSubKey($@"{tagName}\InstallPath");
                var executable = installPath?.GetValue("ExecutablePath") as string;
                var root = installPath?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(executable))
                {
                    yield return executable;
                }
                else if (!string.IsNullOrWhiteSpace(root))
                {
                    yield return Path.Combine(root, "python.exe");
                }
            }
        }
    }

    internal static string? ParseVersion(RuntimeKind kind, string output)
    {
        if (kind == RuntimeKind.Java)
        {
            var first = output.IndexOf('"');
            var second = first < 0 ? -1 : output.IndexOf('"', first + 1);
            return first >= 0 && second > first ? output[(first + 1)..second] : null;
        }

        if (kind == RuntimeKind.Redis)
        {
            var match = RedisVersionPattern.Match(output);
            return match.Success ? match.Groups[1].Value : null;
        }

        var token = output.Split([' ', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var normalized = token is null ? null : ProviderUtilities.NormalizeVersion(token);
        return normalized is not null && StandardVersionPattern.IsMatch(normalized) ? normalized : null;
    }

    internal static bool IsWindowsAppsDirectory(string directory)
    {
        var windowsApps = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");
        var candidate = Path.GetFullPath(directory.Trim('"'));
        return string.Equals(candidate.TrimEnd(Path.DirectorySeparatorChar), windowsApps, StringComparison.OrdinalIgnoreCase)
            || candidate.StartsWith(windowsApps + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPathUnderDirectory(string candidatePath, string directory)
    {
        try
        {
            var candidate = Path.GetFullPath(candidatePath);
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string GetSource(string executable)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return executable.StartsWith(programFiles, StringComparison.OrdinalIgnoreCase) ? "Program Files" : "PATH/Registry";
    }
}
