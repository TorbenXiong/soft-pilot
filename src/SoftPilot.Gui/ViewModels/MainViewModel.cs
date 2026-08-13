using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;

namespace SoftPilot.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IReadOnlyList<IExternalRuntimeDetector> _detectors;
    private readonly IReadOnlyList<IRuntimeProvider> _providers;
    private readonly IStateStore _state;
    private readonly IOperationCoordinator _operations;
    private readonly IGlobalRuntimeService _global;
    private readonly IShellIntegrationService _shell;
    private readonly IRuntimeModulePreferencesStore _modulePreferences;
    private IReadOnlyList<RuntimeRow> _allManagedRuntimes = [];
    private IReadOnlyList<RuntimeRow> _allExternalRuntimes = [];
    private readonly Dictionary<RuntimeKind, IReadOnlyList<RuntimeRelease>> _recommendedReleases = [];
    private bool _modulePreferencesLoaded;

    public MainViewModel(
        IEnumerable<IExternalRuntimeDetector> detectors,
        IEnumerable<IRuntimeProvider> providers,
        IStateStore state,
        IOperationCoordinator operations,
        IGlobalRuntimeService global,
        IShellIntegrationService shell,
        IRuntimeModulePreferencesStore modulePreferences,
        IInstallationLayout layout)
    {
        _detectors = detectors.ToArray();
        _providers = providers.ToArray();
        _state = state;
        _operations = operations;
        _global = global;
        _shell = shell;
        _modulePreferences = modulePreferences;
        RootPath = layout.Root;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRun);
        InstallCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
        UseSelectedCommand = new AsyncRelayCommand(UseSelectedAsync, CanUseSelected);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync, CanUninstallSelected);
        RestoreSelectedCommand = new AsyncRelayCommand(RestoreSelectedAsync, CanRestoreSelected);
        EnableShellCommand = new AsyncRelayCommand(EnableShellAsync, CanRun);
        DisableShellCommand = new AsyncRelayCommand(DisableShellAsync, CanRun);
        SaveModulePreferencesCommand = new AsyncRelayCommand(SaveModulePreferencesAsync, CanRun);
    }

    public string RootPath { get; }
    public ObservableCollection<RuntimeRow> ManagedRuntimes { get; } = [];
    public ObservableCollection<RuntimeRow> ExternalRuntimes { get; } = [];
    public ObservableCollection<RuntimeVersionOption> RecommendedVersions { get; } = [];
    public ObservableCollection<TaskRow> Tasks { get; } = [];

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand UseSelectedCommand { get; }
    public IAsyncRelayCommand UninstallSelectedCommand { get; }
    public IAsyncRelayCommand RestoreSelectedCommand { get; }
    public IAsyncRelayCommand EnableShellCommand { get; }
    public IAsyncRelayCommand DisableShellCommand { get; }
    public IAsyncRelayCommand SaveModulePreferencesCommand { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeDisplayName))]
    [NotifyPropertyChangedFor(nameof(RuntimeInstallDescription))]
    public partial RuntimeKind SelectedRuntimeKind { get; private set; } = RuntimeKind.Node;

    [ObservableProperty]
    public partial RuntimeVersionOption? SelectedRecommendedVersion { get; set; }

    [ObservableProperty]
    public partial string RecommendedVersionHint { get; set; } = "正在读取官方版本目录…";

    [ObservableProperty]
    public partial bool MakeCurrentAfterInstall { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NodeModuleVisibility))]
    [NotifyPropertyChangedFor(nameof(EnabledModuleSummary))]
    public partial bool NodeModuleEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(JavaModuleVisibility))]
    [NotifyPropertyChangedFor(nameof(EnabledModuleSummary))]
    public partial bool JavaModuleEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PythonModuleVisibility))]
    [NotifyPropertyChangedFor(nameof(EnabledModuleSummary))]
    public partial bool PythonModuleEnabled { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial InfoBarSeverity StatusSeverity { get; set; } = InfoBarSeverity.Informational;

    [ObservableProperty]
    public partial string ShellStatusText { get; set; } = "正在检查…";

    [ObservableProperty]
    public partial RuntimeRow? SelectedRuntime { get; set; }

    public bool HasStatus => StatusMessage.Length > 0;
    public Visibility ProgressVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NodeModuleVisibility => NodeModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility JavaModuleVisibility => JavaModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PythonModuleVisibility => PythonModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public string EnabledModuleSummary => $"左侧将显示 {GetEnabledModuleCount()} 个运行时模块";

    public string RuntimeDisplayName => SelectedRuntimeKind switch
    {
        RuntimeKind.Node => "Node.js",
        RuntimeKind.Java => "Java",
        RuntimeKind.Python => "Python",
        _ => SelectedRuntimeKind.ToString(),
    };

    public string RuntimeInstallDescription => SelectedRuntimeKind switch
    {
        RuntimeKind.Node => "从官方目录推荐最近两个 LTS 主版本线的最新稳定补丁。",
        RuntimeKind.Java => "从 Eclipse Temurin 官方目录推荐各个 LTS 版本线的最新稳定 JDK。",
        RuntimeKind.Python => "Python 没有 LTS；这里推荐仍在近期支持范围内的稳定分支最新补丁。",
        _ => "从官方目录选择推荐版本。",
    };

    public bool IsModuleEnabled(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => NodeModuleEnabled,
        RuntimeKind.Java => JavaModuleEnabled,
        RuntimeKind.Python => PythonModuleEnabled,
        _ => false,
    };

    public void SelectRuntimeModule(RuntimeKind kind)
    {
        if (SelectedRuntimeKind == kind)
        {
            ApplyRuntimeFilter();
            return;
        }

        SelectedRuntime = null;
        SelectedRuntimeKind = kind;
    }

    public async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            var preferencesWarning = await LoadModulePreferencesAsync();
            await RefreshRuntimeDataAsync(includeExternal: true);
            var catalogWarnings = await RefreshRecommendedVersionsAsync();
            await RefreshTasksAsync();
            await RefreshShellStatusAsync();
            var warnings = new[] { preferencesWarning }
                .Concat(catalogWarnings)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .ToArray();
            SetStatus(
                warnings.Length == 0 ? "状态和官方版本目录已刷新。" : string.Join(" ", warnings),
                warnings.Length == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        });
    }

    private async Task<string?> LoadModulePreferencesAsync()
    {
        if (_modulePreferencesLoaded)
        {
            return null;
        }

        var preferences = RuntimeModulePreferences.Default;
        string? warning = null;
        try
        {
            preferences = await _modulePreferences.LoadAsync();
        }
        catch (SoftPilotException exception)
        {
            warning = exception.Message;
        }

        NodeModuleEnabled = preferences.NodeEnabled;
        JavaModuleEnabled = preferences.JavaEnabled;
        PythonModuleEnabled = preferences.PythonEnabled;
        _modulePreferencesLoaded = true;
        return warning;
    }

    private async Task SaveModulePreferencesAsync()
    {
        await RunBusyAsync(async () =>
        {
            var preferences = new RuntimeModulePreferences(
                NodeModuleEnabled,
                JavaModuleEnabled,
                PythonModuleEnabled);
            await _modulePreferences.SaveAsync(preferences);
            SetStatus("模块显示配置已保存。隐藏模块不会卸载已有版本。", InfoBarSeverity.Success);
        });
    }

    private async Task InstallAsync()
    {
        if (SelectedRecommendedVersion is null)
        {
            SetStatus("请先从官方推荐列表中选择一个版本。", InfoBarSeverity.Warning);
            return;
        }

        var target = new RuntimeTarget(SelectedRuntimeKind, SelectedRecommendedVersion.Version);

        await RunBusyAsync(async () =>
        {
            var progress = new Progress<OperationProgress>(value =>
                SetStatus(value.Detail ?? value.Stage, InfoBarSeverity.Informational));
            await _operations.InstallAsync(target, MakeCurrentAfterInstall, progress);
            SetStatus($"{target} 安装完成。", InfoBarSeverity.Success);
            await RefreshCoreAsync();
        });
    }

    private async Task UseSelectedAsync()
    {
        var selected = SelectedRuntime!;
        await RunBusyAsync(async () =>
        {
            await _global.UseAsync(selected.RuntimeKind, selected.Version);
            SetStatus($"已切换到 {selected.Kind}@{selected.Version}。", InfoBarSeverity.Success);
            await RefreshCoreAsync();
        });
    }

    private async Task UninstallSelectedAsync()
    {
        var selected = SelectedRuntime!;
        await RunBusyAsync(async () =>
        {
            await _operations.UninstallAsync(new RuntimeTarget(selected.RuntimeKind, selected.Version));
            SetStatus($"{selected.Kind}@{selected.Version} 已移入回收站。", InfoBarSeverity.Success);
            await RefreshCoreAsync();
        });
    }

    private async Task RestoreSelectedAsync()
    {
        var selected = SelectedRuntime!;
        await RunBusyAsync(async () =>
        {
            await _operations.RestoreAsync(new RuntimeTarget(selected.RuntimeKind, selected.Version));
            SetStatus($"{selected.Kind}@{selected.Version} 已恢复。", InfoBarSeverity.Success);
            await RefreshCoreAsync();
        });
    }

    private async Task EnableShellAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _shell.EnableAsync();
            await RefreshShellStatusAsync();
            SetStatus("Shell 集成已启用；请打开新终端。", InfoBarSeverity.Success);
        });
    }

    private async Task DisableShellAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _shell.DisableAsync();
            await RefreshShellStatusAsync();
            SetStatus("原 PATH 与 JAVA_HOME 已恢复。", InfoBarSeverity.Success);
        });
    }

    private async Task RefreshCoreAsync()
    {
        await RefreshRuntimeDataAsync(includeExternal: false);
        await RefreshTasksAsync();
        SelectedRuntime = null;
    }

    private async Task RefreshRuntimeDataAsync(bool includeExternal)
    {
        var managed = await _state.GetInstallationsAsync(includeDeleted: true);
        _allManagedRuntimes = managed.Select(item => new RuntimeRow(
            item.Kind.ToString().ToLowerInvariant(),
            item.Version,
            item.IsDeleted ? "回收站" : item.IsCurrent ? "当前" : "已安装",
            item.IsDeleted ? item.TrashPath ?? item.InstallPath : item.InstallPath,
            item.Kind,
            item.IsCurrent,
            item.IsDeleted)).ToArray();

        if (includeExternal)
        {
            var external = new List<ExternalRuntime>();
            foreach (var detector in _detectors)
            {
                external.AddRange(await detector.DetectAsync());
            }

            _allExternalRuntimes = external.Select(item => new RuntimeRow(
                item.Kind.ToString().ToLowerInvariant(),
                item.Version,
                "外部只读",
                item.ExecutablePath,
                item.Kind,
                false,
                false)).ToArray();
        }

        ApplyRuntimeFilter();
    }

    private async Task<IReadOnlyList<string>> RefreshRecommendedVersionsAsync()
    {
        RecommendedVersionHint = "正在读取官方版本目录…";
        var tasks = _providers.Select(async provider =>
        {
            try
            {
                var available = await provider.GetAvailableAsync();
                return new RuntimeCatalogResult(
                    provider.Kind,
                    RecommendedRuntimeReleaseSelector.Select(provider.Kind, available),
                    null);
            }
            catch (Exception exception)
            {
                return new RuntimeCatalogResult(
                    provider.Kind,
                    null,
                    $"{GetRuntimeDisplayName(provider.Kind)} 官方版本目录加载失败：{exception.Message}");
            }
        });

        var results = await Task.WhenAll(tasks);
        foreach (var result in results.Where(result => result.Releases is not null))
        {
            _recommendedReleases[result.Kind] = result.Releases!;
        }

        ApplyRecommendedVersionOptions();
        return results
            .Where(result => result.Error is not null)
            .Select(result => result.Error!)
            .ToArray();
    }

    private async Task RefreshTasksAsync()
    {
        var operations = await _state.GetOperationsAsync();
        Replace(Tasks, operations.Select(item => new TaskRow(
            item.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            item.Status.ToString(),
            item.Name,
            item.Kind is null ? "-" : $"{item.Kind.Value.ToString().ToLowerInvariant()}@{item.Version}")));
    }

    private void ApplyRuntimeFilter()
    {
        Replace(ManagedRuntimes, _allManagedRuntimes.Where(item => item.RuntimeKind == SelectedRuntimeKind));
        Replace(ExternalRuntimes, _allExternalRuntimes.Where(item => item.RuntimeKind == SelectedRuntimeKind));
        SelectedRuntime = null;
        ApplyRecommendedVersionOptions();
    }

    private void ApplyRecommendedVersionOptions()
    {
        var selectedVersion = SelectedRecommendedVersion?.Version;
        var releases = _recommendedReleases.GetValueOrDefault(SelectedRuntimeKind, []);
        var options = releases.Select(CreateVersionOption).ToArray();
        Replace(RecommendedVersions, options);
        SelectedRecommendedVersion = options.FirstOrDefault(option =>
                                         string.Equals(option.Version, selectedVersion, StringComparison.OrdinalIgnoreCase))
                                     ?? options.FirstOrDefault();
        RecommendedVersionHint = options.Length == 0
            ? "未加载到推荐版本。请检查网络和官方目录后点击右上角“刷新”。"
            : SelectedRuntimeKind switch
            {
                RuntimeKind.Node => "仅显示最近两个 Node.js LTS 主版本线，每条版本线自动选择最新稳定补丁。",
                RuntimeKind.Java => "仅显示 Eclipse Temurin LTS 版本线，每条版本线自动选择最新稳定 JDK。",
                RuntimeKind.Python => "Python 没有 LTS；仅显示最新五个稳定分支，每条分支自动选择最新补丁。",
                _ => "已从官方目录筛选推荐版本。",
            };
    }

    private RuntimeVersionOption CreateVersionOption(RuntimeRelease release)
    {
        var managed = _allManagedRuntimes.FirstOrDefault(item =>
            item.RuntimeKind == release.Kind
            && string.Equals(item.Version, release.Version, StringComparison.OrdinalIgnoreCase));
        var line = GetReleaseLine(release.Kind, release.Version);
        var label = release.Kind switch
        {
            RuntimeKind.Node => $"Node.js {line} LTS — {release.Version}",
            RuntimeKind.Java => $"JDK {line} LTS — {release.Version}",
            RuntimeKind.Python => $"Python {line} 稳定版 — {release.Version}",
            _ => release.Version,
        };
        if (managed is not null)
        {
            label += $" · {managed.State}";
        }

        return new RuntimeVersionOption(release.Version, label, managed is not null);
    }

    private async Task RefreshShellStatusAsync()
    {
        var status = await _shell.GetStatusAsync();
        ShellStatusText = status.IsEnabled
            ? status.Problem ?? "已启用；shims 位于用户 PATH 前部。"
            : "未启用。SoftPilot 不会自动修改用户环境变量。";
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        NotifyCommands();
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            SetStatus(exception.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private int GetEnabledModuleCount() =>
        (NodeModuleEnabled ? 1 : 0) +
        (JavaModuleEnabled ? 1 : 0) +
        (PythonModuleEnabled ? 1 : 0);

    private bool CanRun() => !IsBusy;
    private bool CanInstall() => !IsBusy && SelectedRecommendedVersion is { IsManaged: false };
    private bool CanUseSelected() => !IsBusy && SelectedRuntime is { IsDeleted: false, IsCurrent: false };
    private bool CanUninstallSelected() => !IsBusy && SelectedRuntime is { IsDeleted: false, IsCurrent: false };
    private bool CanRestoreSelected() => !IsBusy && SelectedRuntime is { IsDeleted: true };

    partial void OnSelectedRuntimeKindChanged(RuntimeKind value) => ApplyRuntimeFilter();
    partial void OnSelectedRecommendedVersionChanged(RuntimeVersionOption? value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnSelectedRuntimeChanged(RuntimeRow? value) => NotifyCommands();

    private static string GetRuntimeDisplayName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => "Node.js",
        RuntimeKind.Java => "Java",
        RuntimeKind.Python => "Python",
        _ => kind.ToString(),
    };

    private static string GetReleaseLine(RuntimeKind kind, string version)
    {
        var parts = version.Split('.');
        return kind == RuntimeKind.Python && parts.Length >= 2
            ? $"{parts[0]}.{parts[1]}"
            : parts[0];
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        UseSelectedCommand.NotifyCanExecuteChanged();
        UninstallSelectedCommand.NotifyCanExecuteChanged();
        RestoreSelectedCommand.NotifyCanExecuteChanged();
        EnableShellCommand.NotifyCanExecuteChanged();
        DisableShellCommand.NotifyCanExecuteChanged();
        SaveModulePreferencesCommand.NotifyCanExecuteChanged();
    }

    private void SetStatus(string message, InfoBarSeverity severity)
    {
        StatusMessage = message;
        StatusSeverity = severity;
    }

    private static void Replace<T>(ObservableCollection<T> collection, IEnumerable<T> values)
    {
        collection.Clear();
        foreach (var value in values)
        {
            collection.Add(value);
        }
    }
}

public sealed record RuntimeRow(
    string Kind,
    string Version,
    string State,
    string Path,
    RuntimeKind RuntimeKind,
    bool IsCurrent,
    bool IsDeleted);

public sealed record RuntimeVersionOption(string Version, string DisplayName, bool IsManaged);

public sealed record TaskRow(string StartedAt, string Status, string Name, string Target);

internal sealed record RuntimeCatalogResult(
    RuntimeKind Kind,
    IReadOnlyList<RuntimeRelease>? Releases,
    string? Error);
