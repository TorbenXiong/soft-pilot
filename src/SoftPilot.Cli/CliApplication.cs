using System.CommandLine;
using System.Text.Json;
using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;

namespace SoftPilot.Cli;

public sealed class CliApplication
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly IReadOnlyDictionary<RuntimeKind, IRuntimeProvider> _providers;
    private readonly IReadOnlyList<IExternalRuntimeDetector> _detectors;
    private readonly IStateStore _state;
    private readonly IOperationCoordinator _operations;
    private readonly IGlobalRuntimeService _global;
    private readonly IShellIntegrationService _shell;
    private readonly IDoctorService _doctor;
    private readonly ICacheService _cache;

    public CliApplication(
        IEnumerable<IRuntimeProvider> providers,
        IEnumerable<IExternalRuntimeDetector> detectors,
        IStateStore state,
        IOperationCoordinator operations,
        IGlobalRuntimeService global,
        IShellIntegrationService shell,
        IDoctorService doctor,
        ICacheService cache)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _detectors = detectors.ToArray();
        _state = state;
        _operations = operations;
        _global = global;
        _shell = shell;
        _doctor = doctor;
        _cache = cache;
    }

    public Task<int> RunAsync(string[] args)
    {
        var root = BuildRootCommand();
        return root.Parse(args).InvokeAsync();
    }

    private RootCommand BuildRootCommand()
    {
        var root = new RootCommand("SoftPilot Windows 开发运行时管理器");
        root.Subcommands.Add(BuildRuntimeCommand());
        root.Subcommands.Add(BuildUseCommand());
        root.Subcommands.Add(BuildCurrentCommand());
        root.Subcommands.Add(BuildShellCommand());
        root.Subcommands.Add(BuildDoctorCommand());
        root.Subcommands.Add(BuildTaskCommand());
        root.Subcommands.Add(BuildCacheCommand());
        return root;
    }

    private Command BuildRuntimeCommand()
    {
        var runtime = new Command("runtime", "查询和管理运行时");
        runtime.Subcommands.Add(BuildAvailableCommand());
        runtime.Subcommands.Add(BuildRuntimeListCommand());
        runtime.Subcommands.Add(BuildInstallCommand());
        runtime.Subcommands.Add(BuildTargetCommand("uninstall", "软删除已安装版本", _operations.UninstallAsync));
        runtime.Subcommands.Add(BuildTargetCommand("restore", "恢复七日内软删除版本", _operations.RestoreAsync));
        return runtime;
    }

    private Command BuildInstallCommand()
    {
        var targetArgument = TargetArgument(allowNodeAlias: true);
        var command = new Command("install", "安装精确版本、Node.js LTS 或指定主版本") { targetArgument };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var requested = ParseTarget(parseResult.GetRequiredValue(targetArgument));
            var target = await ResolveInstallTargetAsync(requested, cancellationToken);
            await _operations.InstallAsync(target, makeCurrent: false, new ConsoleProgress(), cancellationToken);
            Console.WriteLine($"install {target} 完成。");
        }));
        return command;
    }

    private Command BuildAvailableCommand()
    {
        var kindArgument = new Argument<string?>("kind")
        {
            Description = "node、java 或 python；省略时查询全部",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var jsonOption = JsonOption();
        var command = new Command("available", "查询官方可安装版本") { kindArgument, jsonOption };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var kindText = parseResult.GetValue(kindArgument);
            var selected = kindText is null
                ? _providers.Values
                : [_providers[ParseKind(kindText)]];
            var releases = new List<RuntimeRelease>();
            foreach (var provider in selected)
            {
                releases.AddRange(await provider.GetAvailableAsync(cancellationToken));
            }

            Write(releases, parseResult.GetValue(jsonOption), release =>
                $"{release.Kind.ToString().ToLowerInvariant(),-7} {release.Version,-24} x64{(release.IsLongTermSupport ? " LTS" : string.Empty)}");
        }));
        return command;
    }

    private Command BuildRuntimeListCommand()
    {
        var managedOption = new Option<bool>("--managed") { Description = "仅显示 SoftPilot 管理的版本" };
        var externalOption = new Option<bool>("--external") { Description = "仅显示外部只读运行时" };
        var jsonOption = JsonOption();
        var command = new Command("list", "列出已发现运行时") { managedOption, externalOption, jsonOption };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var managedOnly = parseResult.GetValue(managedOption);
            var externalOnly = parseResult.GetValue(externalOption);
            if (managedOnly && externalOnly)
            {
                throw new ArgumentException("--managed 与 --external 不能同时使用。");
            }

            var managed = externalOnly
                ? []
                : await _state.GetInstallationsAsync(includeDeleted: false, cancellationToken);
            var external = new List<ExternalRuntime>();
            if (!managedOnly)
            {
                foreach (var detector in _detectors)
                {
                    external.AddRange(await detector.DetectAsync(cancellationToken));
                }
            }

            if (parseResult.GetValue(jsonOption))
            {
                Console.WriteLine(JsonSerializer.Serialize(new { managed, external }, JsonOptions));
                return;
            }

            foreach (var item in managed)
            {
                Console.WriteLine($"managed  {item.Kind.ToString().ToLowerInvariant(),-7} {item.Version,-24}{(item.IsCurrent ? " current" : string.Empty)}");
            }

            foreach (var item in external)
            {
                Console.WriteLine($"external {item.Kind.ToString().ToLowerInvariant(),-7} {item.Version,-24} {item.ExecutablePath}");
            }
        }));
        return command;
    }

    private Command BuildUseCommand()
    {
        var targetArgument = TargetArgument(allowNodeAlias: true);
        var globalOption = new Option<bool>("--global") { Description = "切换全局 current 链接", Required = true };
        var command = new Command("use", "切换全局版本") { targetArgument, globalOption };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var requested = ParseTarget(parseResult.GetRequiredValue(targetArgument));
            var target = await ResolveUseTargetAsync(requested, cancellationToken);
            await _global.UseAsync(target.Kind, target.Version, cancellationToken);
            Console.WriteLine($"已切换到 {target}。");
        }));
        return command;
    }

    private Command BuildCurrentCommand()
    {
        var jsonOption = JsonOption();
        var command = new Command("current", "显示当前全局版本") { jsonOption };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var current = await _global.GetCurrentAsync(cancellationToken);
            Write(current, parseResult.GetValue(jsonOption), pair =>
                $"{pair.Key.ToString().ToLowerInvariant(),-7} {pair.Value?.Version ?? "-"}");
        }));
        return command;
    }

    private Command BuildShellCommand()
    {
        var shell = new Command("shell", "管理用户 Shell 集成");
        shell.Subcommands.Add(SimpleCommand("enable", "启用 shim PATH 和 JAVA_HOME", async token =>
        {
            await _shell.EnableAsync(token);
            Console.WriteLine("Shell 集成已启用；新终端将读取更新后的环境。");
        }));
        shell.Subcommands.Add(SimpleCommand("disable", "恢复启用前的 PATH 和 JAVA_HOME", async token =>
        {
            await _shell.DisableAsync(token);
            Console.WriteLine("Shell 环境已从快照恢复。");
        }));

        var statusJson = JsonOption();
        var status = new Command("status", "显示 Shell 集成状态") { statusJson };
        status.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var value = await _shell.GetStatusAsync(cancellationToken);
            Write(value, parseResult.GetValue(statusJson), item =>
                $"enabled={item.IsEnabled} path-first={item.IsShimPathFirst} JAVA_HOME={item.JavaHome ?? "-"}{(item.Problem is null ? string.Empty : $" problem={item.Problem}")}");
        }));
        shell.Subcommands.Add(status);
        return shell;
    }

    private Command BuildDoctorCommand()
    {
        var jsonOption = JsonOption();
        var command = new Command("doctor", "检查 SoftPilot 工作区和集成状态") { jsonOption };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var checks = await _doctor.RunAsync(cancellationToken);
            Write(checks, parseResult.GetValue(jsonOption), check =>
                $"{(check.Passed ? "PASS" : "FAIL"),-4} {check.Name,-24} {check.Message}");
            if (checks.Any(check => !check.Passed))
            {
                throw new SoftPilot.Application.SoftPilotException("Doctor 检查发现问题。");
            }
        }));
        return command;
    }

    private Command BuildTaskCommand()
    {
        var task = new Command("task", "查看操作历史");
        var listJson = JsonOption();
        var list = new Command("list", "列出最近操作") { listJson };
        list.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var operations = await _state.GetOperationsAsync(cancellationToken: cancellationToken);
            Write(operations, parseResult.GetValue(listJson), operation =>
                $"{operation.Id} {operation.Status,-9} {operation.Name,-10} {operation.Kind?.ToString().ToLowerInvariant()}@{operation.Version}");
        }));
        task.Subcommands.Add(list);

        var id = new Argument<Guid>("id") { Description = "任务 ID" };
        var show = new Command("show", "显示任务详情") { id };
        show.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var operation = await _state.FindOperationAsync(parseResult.GetRequiredValue(id), cancellationToken)
                ?? throw new KeyNotFoundException("没有找到该任务。");
            Console.WriteLine(JsonSerializer.Serialize(operation, JsonOptions));
        }));
        task.Subcommands.Add(show);
        return task;
    }

    private Command BuildCacheCommand()
    {
        var cache = new Command("cache", "管理下载缓存");
        var json = JsonOption();
        var status = new Command("status", "显示缓存大小") { json };
        status.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var value = await _cache.GetStatusAsync(cancellationToken);
            Write(value, parseResult.GetValue(json), item =>
                $"{item.FileCount} files, {item.Bytes:N0} bytes, {item.Path}");
        }));
        cache.Subcommands.Add(status);
        cache.Subcommands.Add(SimpleCommand("clean", "清理已下载归档和校验文件", async token =>
        {
            await _cache.CleanAsync(token);
            Console.WriteLine("下载缓存已清理。");
        }));
        return cache;
    }

    private static Command BuildTargetCommand(
        string name,
        string description,
        Func<RuntimeTarget, CancellationToken, Task> action)
    {
        var targetArgument = TargetArgument();
        var command = new Command(name, description) { targetArgument };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var target = ParseTarget(parseResult.GetRequiredValue(targetArgument));
            await action(target, cancellationToken);
            Console.WriteLine($"{name} {target} 完成。");
        }));
        return command;
    }

    private static Command SimpleCommand(string name, string description, Func<CancellationToken, Task> action)
    {
        var command = new Command(name, description);
        command.SetAction(async (_, cancellationToken) => await GuardAsync(() => action(cancellationToken)));
        return command;
    }

    private static async Task<int> GuardAsync(Func<Task> action)
    {
        try
        {
            await action();
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 130;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"spt: {exception.Message}");
            return 1;
        }
    }

    private static Argument<string> TargetArgument(bool allowNodeAlias = false) => new("target")
    {
        Description = allowNodeAlias
            ? "目标，例如 node@24.19.0、node@24、node@lts"
            : "精确目标，例如 node@24.19.0",
    };

    private static Option<bool> JsonOption() => new("--json") { Description = "输出 JSON" };

    private static RuntimeTarget ParseTarget(string value) => RuntimeTarget.TryParse(value, out var target)
        ? target
        : throw new ArgumentException("目标格式应为 <node|java|python>@<exact-version>。");

    private static RuntimeKind ParseKind(string value) => Enum.TryParse<RuntimeKind>(value, true, out var kind)
        ? kind
        : throw new ArgumentException($"不支持的运行时类型：{value}");

    private async Task<RuntimeTarget> ResolveInstallTargetAsync(
        RuntimeTarget requested,
        CancellationToken cancellationToken)
    {
        if (!NodeRuntimeTargetResolver.IsAlias(requested))
        {
            return NodeRuntimeTargetResolver.ResolveForInstall(requested, []);
        }

        var available = await _providers[RuntimeKind.Node].GetAvailableAsync(cancellationToken);
        return NodeRuntimeTargetResolver.ResolveForInstall(requested, available);
    }

    private async Task<RuntimeTarget> ResolveUseTargetAsync(
        RuntimeTarget requested,
        CancellationToken cancellationToken)
    {
        if (!NodeRuntimeTargetResolver.IsAlias(requested))
        {
            return NodeRuntimeTargetResolver.ResolveForUse(requested, []);
        }

        var installed = await _state.GetInstallationsAsync(cancellationToken: cancellationToken);
        IReadOnlyList<RuntimeRelease>? available = null;
        if (string.Equals(requested.Version, "lts", StringComparison.OrdinalIgnoreCase))
        {
            available = await _providers[RuntimeKind.Node].GetAvailableAsync(cancellationToken);
        }

        return NodeRuntimeTargetResolver.ResolveForUse(requested, installed, available);
    }

    private static void Write<T>(T value, bool json, Func<T, string> text)
    {
        Console.WriteLine(json ? JsonSerializer.Serialize(value, JsonOptions) : text(value));
    }

    private static void Write<T>(IEnumerable<T> values, bool json, Func<T, string> text)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(values, JsonOptions));
            return;
        }

        foreach (var value in values)
        {
            Console.WriteLine(text(value));
        }
    }

    private sealed class ConsoleProgress : IProgress<OperationProgress>
    {
        private string? _lastStage;
        private string? _lastDetail;
        private int? _lastPercentage;

        public void Report(OperationProgress value)
        {
            int? roundedPercentage = value.Percentage is null
                ? null
                : (int)Math.Clamp(Math.Floor(value.Percentage.Value), 0, 100);
            if (string.Equals(_lastStage, value.Stage, StringComparison.Ordinal)
                && string.Equals(_lastDetail, value.Detail, StringComparison.Ordinal)
                && _lastPercentage == roundedPercentage)
            {
                return;
            }

            _lastStage = value.Stage;
            _lastDetail = value.Detail;
            _lastPercentage = roundedPercentage;
            var percentage = roundedPercentage is null ? string.Empty : $" {roundedPercentage}%";
            Console.Error.WriteLine($"[{value.Stage}]{percentage} {value.Detail}".TrimEnd());
        }
    }
}
