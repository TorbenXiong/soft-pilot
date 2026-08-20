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
    private readonly IRedisServiceManager _redis;
    private readonly IMySqlServiceManager _mySql;

    public CliApplication(
        IEnumerable<IRuntimeProvider> providers,
        IEnumerable<IExternalRuntimeDetector> detectors,
        IStateStore state,
        IOperationCoordinator operations,
        IGlobalRuntimeService global,
        IShellIntegrationService shell,
        IDoctorService doctor,
        ICacheService cache,
        IRedisServiceManager redis,
        IMySqlServiceManager mySql)
    {
        _providers = providers.ToDictionary(provider => provider.Kind);
        _detectors = detectors.ToArray();
        _state = state;
        _operations = operations;
        _global = global;
        _shell = shell;
        _doctor = doctor;
        _cache = cache;
        _redis = redis;
        _mySql = mySql;
    }

    public Task<int> RunAsync(string[] args)
    {
        var root = BuildRootCommand();
        return root.Parse(args).InvokeAsync();
    }

    private RootCommand BuildRootCommand()
    {
        var root = new RootCommand("SoftPilot Windows 开发运行时与本地服务管理器");
        root.Subcommands.Add(BuildRuntimeCommand());
        root.Subcommands.Add(BuildUseCommand());
        root.Subcommands.Add(BuildCurrentCommand());
        root.Subcommands.Add(BuildShellCommand());
        root.Subcommands.Add(BuildDoctorCommand());
        root.Subcommands.Add(BuildTaskCommand());
        root.Subcommands.Add(BuildCacheCommand());
        root.Subcommands.Add(BuildRedisCommand());
        root.Subcommands.Add(BuildMySqlCommand());
        return root;
    }

    private Command BuildRuntimeCommand()
    {
        var runtime = new Command("runtime", "查询和管理运行时");
        runtime.Subcommands.Add(BuildAvailableCommand());
        runtime.Subcommands.Add(BuildRuntimeListCommand());
        runtime.Subcommands.Add(BuildInstallCommand());
        runtime.Subcommands.Add(BuildUninstallCommand());
        return runtime;
    }

    private Command BuildUninstallCommand()
    {
        var targetArgument = TargetArgument();
        var deleteDataOption = new Option<bool>("--delete-data")
        {
            Description = "卸载 Redis 或 MySQL 时同时永久删除对应版本线的数据、配置、凭据和日志",
        };
        var command = new Command("uninstall", "卸载并删除已安装版本")
        {
            targetArgument,
            deleteDataOption,
        };
        command.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var target = ParseTarget(parseResult.GetRequiredValue(targetArgument));
            var deleteData = parseResult.GetValue(deleteDataOption);
            await _operations.UninstallAsync(
                target,
                new RuntimeUninstallOptions(deleteData),
                cancellationToken);
            Console.WriteLine($"uninstall {target} 完成。" + (deleteData ? " 服务数据、配置、凭据和日志已删除。" : string.Empty));
        }));
        return command;
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
            Description = "node、java、python、redis 或 mysql；省略时查询全部",
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
        var shell = new Command("shell", "查看自动终端集成状态");
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

    private Command BuildRedisCommand()
    {
        var redis = new Command("redis", "管理本地 Redis 服务");

        var statusJson = JsonOption();
        var status = new Command("status", "显示 Redis 服务状态") { statusJson };
        status.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var value = await _redis.GetStatusAsync(cancellationToken);
            Write(value, parseResult.GetValue(statusJson), item => item.IsRunning
                ? $"running redis@{item.Version} pid={item.ProcessId} config={item.ConfigPath} data={item.DataPath} log={item.LogPath}"
                : $"stopped{(item.Problem is null ? string.Empty : $" problem={item.Problem}")}");
        }));
        redis.Subcommands.Add(status);

        var versionArgument = new Argument<string?>("version")
        {
            Description = "已安装的精确版本；省略时使用当前 Redis 版本",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var start = new Command("start", "启动 Redis 服务") { versionArgument };
        start.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var version = parseResult.GetValue(versionArgument);
            if (string.IsNullOrWhiteSpace(version))
            {
                var current = await _global.GetCurrentAsync(cancellationToken);
                version = current[RuntimeKind.Redis]?.Version
                    ?? throw new SoftPilotException("尚未选择当前 Redis 版本；请传入版本或先执行 spt use redis@<version> --global。");
            }

            var value = await _redis.StartAsync(version, cancellationToken);
            Console.WriteLine($"Redis {value.Version} 已启动，PID {value.ProcessId}。");
        }));
        redis.Subcommands.Add(start);

        redis.Subcommands.Add(SimpleCommand("stop", "停止 Redis 服务", async token =>
        {
            await _redis.StopAsync(token);
            Console.WriteLine("Redis 已停止。");
        }));
        return redis;
    }

    private Command BuildMySqlCommand()
    {
        var mysql = new Command("mysql", "管理本地 MySQL 服务");

        var statusJson = JsonOption();
        var status = new Command("status", "显示 MySQL 服务状态") { statusJson };
        status.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var values = await _mySql.GetStatusesAsync(cancellationToken);
            Write(values, parseResult.GetValue(statusJson), item => item.IsRunning
                ? $"running mysql@{item.Version} port={item.Port} pid={item.ProcessId} config={item.ConfigPath} data={item.DataPath} log={item.LogPath}"
                : $"stopped mysql@{item.Version} port={item.Port}{(item.Problem is null ? string.Empty : $" problem={item.Problem}")}");
        }));
        mysql.Subcommands.Add(status);

        var versionArgument = new Argument<string?>("version")
        {
            Description = "已安装的精确版本；省略时使用当前 MySQL 版本",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var start = new Command("start", "启动 MySQL 服务") { versionArgument };
        start.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var version = await ResolveServiceVersionAsync(
                RuntimeKind.MySql,
                parseResult.GetValue(versionArgument),
                cancellationToken);
            var value = await _mySql.StartAsync(version, cancellationToken);
            Console.WriteLine($"MySQL {value.Version} 已启动，PID {value.ProcessId}。首次初始化会生成仅当前 Windows 用户可解密的 root 凭据；使用 spt mysql credentials 查看。");
        }));
        mysql.Subcommands.Add(start);

        var stopVersionArgument = new Argument<string?>("version")
        {
            Description = "已安装的精确版本；省略时使用当前 MySQL 版本",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var stop = new Command("stop", "停止指定 MySQL 服务") { stopVersionArgument };
        stop.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var version = await ResolveServiceVersionAsync(
                RuntimeKind.MySql,
                parseResult.GetValue(stopVersionArgument),
                cancellationToken);
            await _mySql.StopAsync(version, cancellationToken);
            Console.WriteLine($"MySQL {version} 已停止。");
        }));
        mysql.Subcommands.Add(stop);

        var credentialsJson = JsonOption();
        var credentialsVersion = new Argument<string?>("version")
        {
            Description = "已初始化的精确版本；省略时使用当前 MySQL 版本",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var credentials = new Command("credentials", "显示当前 Windows 用户可解密的本地 root 凭据")
        {
            credentialsVersion,
            credentialsJson,
        };
        credentials.SetAction(async (parseResult, cancellationToken) => await GuardAsync(async () =>
        {
            var version = await ResolveServiceVersionAsync(
                RuntimeKind.MySql,
                parseResult.GetValue(credentialsVersion),
                cancellationToken);
            var value = await _mySql.GetCredentialsAsync(version, cancellationToken);
            if (parseResult.GetValue(credentialsJson))
            {
                Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
            }
            else
            {
                Console.WriteLine($"host={value.Host}{Environment.NewLine}port={value.Port}{Environment.NewLine}user={value.UserName}{Environment.NewLine}password={value.Password}");
            }
        }));
        mysql.Subcommands.Add(credentials);
        return mysql;
    }

    private async Task<string> ResolveServiceVersionAsync(
        RuntimeKind kind,
        string? version,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            return version;
        }

        var current = await _global.GetCurrentAsync(cancellationToken);
        return current[kind]?.Version
            ?? throw new SoftPilotException($"尚未选择当前 {kind} 版本；请传入版本或先执行 spt use {kind.ToString().ToLowerInvariant()}@<version> --global。");
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
        : throw new ArgumentException("目标格式应为 <node|java|python|redis|mysql>@<exact-version>。");

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
