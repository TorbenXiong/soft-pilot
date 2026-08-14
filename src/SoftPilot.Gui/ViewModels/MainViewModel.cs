using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
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
    private readonly IRuntimeModulePreferencesStore _modulePreferences;
    private IReadOnlyList<RuntimeRow> _allManagedRuntimes = [];
    private IReadOnlyList<RuntimeRow> _allExternalRuntimes = [];
    private readonly Dictionary<RuntimeKind, IReadOnlyList<RuntimeRelease>> _recommendedReleases = [];
    private readonly Dictionary<RuntimeTarget, RuntimeOperationFeedback> _runtimeFeedback = [];
    private readonly HashSet<RuntimeTarget> _installingTargets = [];
    private bool _modulePreferencesLoaded;

    public MainViewModel(
        IEnumerable<IExternalRuntimeDetector> detectors,
        IEnumerable<IRuntimeProvider> providers,
        IStateStore state,
        IOperationCoordinator operations,
        IGlobalRuntimeService global,
        IRuntimeModulePreferencesStore modulePreferences)
    {
        _detectors = detectors.ToArray();
        _providers = providers.ToArray();
        _state = state;
        _operations = operations;
        _global = global;
        _modulePreferences = modulePreferences;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRun);
        InstallCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
        UseSelectedCommand = new AsyncRelayCommand(UseSelectedAsync, CanUseSelected);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync, CanUninstallSelected);
        SaveModulePreferencesCommand = new AsyncRelayCommand(SaveModulePreferencesAsync, CanRun);
        LanguageOptions =
        [
            new LanguageOption("zh-CN", "简体中文"),
            new LanguageOption("en-US", "English"),
        ];
        SelectedLanguage = LanguageOptions[0];
        ReplaceModuleSettings(RuntimeModulePreferences.Default);
    }

    public ObservableCollection<RuntimeRow> ManagedRuntimes { get; } = [];
    public ObservableCollection<RuntimeRow> ExternalRuntimes { get; } = [];
    public ObservableCollection<InstalledRuntimeRow> InstalledRuntimes { get; } = [];
    public ObservableCollection<RuntimeVersionOption> RecommendedVersions { get; } = [];
    public ObservableCollection<RuntimeVersionRow> VersionRows { get; } = [];
    public ObservableCollection<TaskRow> Tasks { get; } = [];
    public ObservableCollection<RuntimeModuleSetting> ModuleSettings { get; } = [];
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand UseSelectedCommand { get; }
    public IAsyncRelayCommand UninstallSelectedCommand { get; }
    public IAsyncRelayCommand SaveModulePreferencesCommand { get; }

    public event Action<UserNotification>? NotificationRequested;
    public event Action? ModulePreferencesChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeDisplayName))]
    [NotifyPropertyChangedFor(nameof(RuntimeInstallDescription))]
    public partial RuntimeKind SelectedRuntimeKind { get; private set; } = RuntimeKind.Node;

    [ObservableProperty]
    public partial RuntimeVersionOption? SelectedRecommendedVersion { get; set; }

    [ObservableProperty]
    public partial string RecommendedVersionHint { get; set; } = "正在读取官方版本目录…";

    public bool NodeModuleEnabled => IsModuleEnabled(RuntimeKind.Node);
    public bool JavaModuleEnabled => IsModuleEnabled(RuntimeKind.Java);
    public bool PythonModuleEnabled => IsModuleEnabled(RuntimeKind.Python);

    [ObservableProperty]
    public partial LanguageOption? SelectedLanguage { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CatalogLoadingVisibility))]
    [NotifyPropertyChangedFor(nameof(VersionRowsEmptyVisibility))]
    public partial bool IsCatalogLoading { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RefreshIconVisibility))]
    [NotifyPropertyChangedFor(nameof(RefreshProgressVisibility))]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty]
    public partial string ModuleSaveStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial Brush ModuleSaveStatusBrush { get; set; } = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);

    [ObservableProperty]
    public partial RuntimeRow? SelectedRuntime { get; set; }

    public Visibility RefreshIconVisibility => IsRefreshing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RefreshProgressVisibility => IsRefreshing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CatalogLoadingVisibility => IsCatalogLoading && VersionRows.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility VersionRowsEmptyVisibility => !IsCatalogLoading && VersionRows.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility InstalledRuntimesEmptyVisibility => InstalledRuntimes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NodeModuleVisibility => NodeModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility JavaModuleVisibility => JavaModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PythonModuleVisibility => PythonModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public bool IsEnglish => SelectedLanguage?.Code == "en-US";
    public string TaskHistoryText => T("任务历史", "Task history");
    public string SettingsText => T("设置", "Settings");
    public string RefreshText => T("刷新", "Refresh");
    public string InstalledTabText => T("已安装", "Installed");
    public string VersionManagementTabText => T("版本管理", "Version management");
    public string NoInstalledText => T("暂无已安装版本", "No installed versions");
    public string NoVersionsText => T("未加载到可管理版本，请刷新后重试", "No versions available. Refresh to try again.");
    public string CatalogLoadingText => T("正在加载版本…", "Loading versions…");
    public string VersionHeaderText => T("版本", "Version");
    public string PathHeaderText => T("路径", "Path");
    public string EnvironmentHeaderText => T("当前版本", "Current version");
    public string ReleaseLineHeaderText => T("版本线", "Release line");
    public string IsInstalledHeaderText => T("是否安装", "Installed");
    public string OperationHeaderText => T("操作", "Action");
    public string TimeHeaderText => T("时间", "Time");
    public string StatusHeaderText => T("状态", "Status");
    public string TaskTypeHeaderText => T("类型", "Type");
    public string TargetHeaderText => T("目标", "Target");
    public string ModulesText => T("模块", "Modules");
    public string SaveModulesText => T("保存模块配置", "Save modules");
    public string LanguageText => T("语言", "Language");

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

    public bool IsModuleEnabled(RuntimeKind kind) =>
        ModuleSettings.FirstOrDefault(item => item.Kind == kind)?.IsEnabled == true;

    public IReadOnlyList<RuntimeKind> GetOrderedModuleKinds() =>
        ModuleSettings.Select(item => item.Kind).ToArray();

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

    public async Task InitializeAsync()
    {
        var preferencesWarning = await LoadModulePreferencesAsync();
        await RefreshRuntimeDataAsync(includeExternal: false);
        await LoadCachedRecommendedVersionsAsync();
        await RefreshTasksAsync();
        _ = RefreshStartupDataAsync(preferencesWarning);
    }

    public async Task RefreshAsync()
    {
        IsRefreshing = true;
        try
        {
            await RunBusyAsync(async () =>
            {
                var preferencesWarning = await LoadModulePreferencesAsync();
                await RefreshRuntimeDataAsync(includeExternal: false);
                var externalTask = RefreshExternalRuntimeDataAsync();
                var catalogTask = RefreshRecommendedVersionsAsync(forceRefresh: true);
                string? externalWarning = null;
                try
                {
                    await externalTask;
                }
                catch (Exception exception)
                {
                    externalWarning = T(
                        $"外部运行时扫描失败：{exception.Message}",
                        $"External runtime scan failed: {exception.Message}");
                }

                var catalogWarnings = await catalogTask;
                await RefreshTasksAsync();
                var warnings = new[] { preferencesWarning, externalWarning }
                    .Concat(catalogWarnings)
                    .Where(message => !string.IsNullOrWhiteSpace(message))
                    .ToArray();
                if (warnings.Length > 0)
                {
                    NotifyUser(T("刷新未完全成功", "Refresh incomplete"), string.Join(" ", warnings), isError: true);
                }
            });
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    private async Task RefreshStartupDataAsync(string? preferencesWarning)
    {
        var catalogTask = RefreshRecommendedVersionsAsync(forceRefresh: false);
        string? externalWarning = null;
        try
        {
            await RefreshExternalRuntimeDataAsync();
        }
        catch (Exception exception)
        {
            externalWarning = T(
                $"外部运行时扫描失败：{exception.Message}",
                $"External runtime scan failed: {exception.Message}");
        }

        var catalogWarnings = await catalogTask;
        var warnings = new[] { preferencesWarning, externalWarning }
            .Concat(catalogWarnings)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToArray();
        if (warnings.Length > 0 && _recommendedReleases.Count == 0)
        {
            NotifyUser(T("加载未完全成功", "Loading incomplete"), string.Join(" ", warnings), isError: true);
        }
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

        ReplaceModuleSettings(preferences);
        SelectedLanguage = LanguageOptions.FirstOrDefault(option => option.Code == preferences.Language)
            ?? LanguageOptions[0];
        NotifyLocalizedProperties();
        _modulePreferencesLoaded = true;
        return warning;
    }

    public async Task SaveModulePreferencesAsync()
    {
        await RunBusyAsync(async () =>
        {
            var preferences = CreateModulePreferences();
            await _modulePreferences.SaveAsync(preferences);
            ModuleSaveStatusBrush = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
            ModuleSaveStatusText = T("已保存", "Saved");
            ModulePreferencesChanged?.Invoke();
        }, showSuccessOrFailureDialog: false,
        onError: exception =>
        {
            ModuleSaveStatusBrush = new SolidColorBrush(Microsoft.UI.Colors.Firebrick);
            ModuleSaveStatusText = T($"保存失败：{exception.Message}", $"Unable to save: {exception.Message}");
        });
    }

    public async Task ChangeLanguageAsync(LanguageOption option)
    {
        if (SelectedLanguage?.Code == option.Code)
        {
            return;
        }

        SelectedLanguage = option;
        NotifyLocalizedProperties();
        ApplyRuntimeFilter();
        await RefreshTasksAsync();

        try
        {
            var preferences = CreateModulePreferences(option.Code);
            await _modulePreferences.SaveAsync(preferences);
        }
        catch (Exception exception)
        {
            NotifyUser(T("语言设置保存失败", "Unable to save language"), exception.Message, isError: true);
        }
    }

    private Task InstallAsync()
    {
        if (SelectedRecommendedVersion is null)
        {
            NotifyUser(
                T("无法安装", "Unable to install"),
                T("请先选择一个版本。", "Select a version first."),
                isError: true);
            return Task.CompletedTask;
        }

        return InstallRuntimeAsync(SelectedRuntimeKind, SelectedRecommendedVersion.Version);
    }

    public Task InstallVersionAsync(RuntimeVersionRow row) =>
        row.CanInstall ? InstallRuntimeAsync(row.RuntimeKind, row.Version) : Task.CompletedTask;

    private async Task InstallRuntimeAsync(RuntimeKind kind, string version)
    {
        var target = new RuntimeTarget(kind, version);
        if (!_installingTargets.Add(target))
        {
            return;
        }

        SetRuntimeFeedback(target, new RuntimeOperationFeedback(
            0,
            T("等待安装…", "Waiting to install…"),
            RuntimeFeedbackKind.Running,
            true));
        Exception? failure = null;
        try
        {
            var progress = new Progress<OperationProgress>(value => SetRuntimeProgress(target, value));
            await _operations.InstallAsync(target, makeCurrent: false, progress);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            _installingTargets.Remove(target);
            if (failure is null)
            {
                ClearRuntimeFeedback(target);
            }
            else
            {
                SetRuntimeFeedback(target, new RuntimeOperationFeedback(
                    GetRuntimeFeedback(target)?.Percentage ?? 0,
                    T($"安装失败：{failure.Message}", $"Installation failed: {failure.Message}"),
                    RuntimeFeedbackKind.Error,
                    false));
            }
            await RefreshCoreAsync();
        }
    }

    private Task UseSelectedAsync() =>
        UseRuntimeAsync(SelectedRuntime!.RuntimeKind, SelectedRuntime.Version);

    public Task UseVersionAsync(RuntimeVersionRow row) =>
        row.CanUse ? UseRuntimeAsync(row.RuntimeKind, row.Version) : Task.CompletedTask;

    public Task UseInstalledRuntimeAsync(InstalledRuntimeRow row) =>
        row.CanToggleEnvironment ? UseRuntimeAsync(row.RuntimeKind, row.Version) : Task.CompletedTask;

    private async Task UseRuntimeAsync(RuntimeKind kind, string version)
    {
        var target = new RuntimeTarget(kind, version);
        SetRuntimeFeedback(target, new RuntimeOperationFeedback(
            0,
            T("正在设置当前版本…", "Setting current version…"),
            RuntimeFeedbackKind.Running,
            true));
        await RunBusyAsync(async () =>
        {
            await _global.UseAsync(kind, version);
            SetRuntimeFeedback(target, new RuntimeOperationFeedback(
                100,
                T("已设为当前版本", "Current version set"),
                RuntimeFeedbackKind.Success,
                false));
            await RefreshCoreAsync();
        }, showSuccessOrFailureDialog: false,
        onError: exception => SetRuntimeFeedback(target, new RuntimeOperationFeedback(
            0,
            T($"设置失败：{exception.Message}", $"Update failed: {exception.Message}"),
            RuntimeFeedbackKind.Error,
            false)));
    }

    public Task ClearGlobalVersionAsync(RuntimeVersionRow row) =>
        row.CanClearGlobal ? ClearGlobalAsync(row.RuntimeKind) : Task.CompletedTask;

    public Task ClearInstalledGlobalAsync(InstalledRuntimeRow row) =>
        row.IsCurrent ? ClearGlobalAsync(row.RuntimeKind) : Task.CompletedTask;

    private async Task ClearGlobalAsync(RuntimeKind kind)
    {
        var current = _allManagedRuntimes.FirstOrDefault(item => item.RuntimeKind == kind && item.IsCurrent);
        RuntimeTarget? target = current is null ? null : new RuntimeTarget(kind, current.Version);
        await RunBusyAsync(async () =>
        {
            await _global.ClearAsync(kind);
            if (target is not null)
            {
                SetRuntimeFeedback(target.Value, new RuntimeOperationFeedback(
                    100,
                    T("已取消当前版本", "Current version cleared"),
                    RuntimeFeedbackKind.Success,
                    false));
            }
            await RefreshCoreAsync();
        }, showSuccessOrFailureDialog: false,
        onError: exception =>
        {
            if (target is not null)
            {
                SetRuntimeFeedback(target.Value, new RuntimeOperationFeedback(
                    0,
                    T($"取消失败：{exception.Message}", $"Unable to clear: {exception.Message}"),
                    RuntimeFeedbackKind.Error,
                    false));
            }
        });
    }

    private Task UninstallSelectedAsync() =>
        UninstallRuntimeAsync(SelectedRuntime!.RuntimeKind, SelectedRuntime.Version);

    public Task UninstallVersionAsync(RuntimeVersionRow row) =>
        row.CanUninstall ? UninstallRuntimeAsync(row.RuntimeKind, row.Version) : Task.CompletedTask;

    public Task UninstallInstalledRuntimeAsync(InstalledRuntimeRow row) =>
        row.CanUninstall ? UninstallRuntimeAsync(row.RuntimeKind, row.Version) : Task.CompletedTask;

    private async Task UninstallRuntimeAsync(RuntimeKind kind, string version)
    {
        var target = new RuntimeTarget(kind, version);
        await RunBusyAsync(async () =>
        {
            await _operations.UninstallAsync(target);
            await RefreshCoreAsync();
        }, showSuccessOrFailureDialog: false,
        onError: exception => SetRuntimeFeedback(target, new RuntimeOperationFeedback(
            0,
            T($"卸载失败：{exception.Message}", $"Uninstall failed: {exception.Message}"),
            RuntimeFeedbackKind.Error,
            false)));
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
            item.IsDeleted ? T("已卸载", "Uninstalled") : item.IsCurrent ? T("当前", "Current") : T("已安装", "Installed"),
            item.IsDeleted ? item.TrashPath ?? item.InstallPath : item.InstallPath,
            item.Kind,
            item.IsCurrent,
            item.IsDeleted)).ToArray();

        if (includeExternal)
        {
            await RefreshExternalRuntimeDataAsync();
            return;
        }

        ApplyRuntimeFilter();
    }

    private async Task RefreshExternalRuntimeDataAsync()
    {
        var detected = await Task.WhenAll(_detectors.Select(detector => detector.DetectAsync()));
        _allExternalRuntimes = detected
            .SelectMany(items => items)
            .Select(item => new RuntimeRow(
                item.Kind.ToString().ToLowerInvariant(),
                item.Version,
                T("外部只读", "External, read-only"),
                item.ExecutablePath,
                item.Kind,
                false,
                false))
            .ToArray();
        ApplyRuntimeFilter();
    }

    private async Task LoadCachedRecommendedVersionsAsync()
    {
        var tasks = _providers
            .OfType<ICachedRuntimeProvider>()
            .Select(provider => provider.GetCachedCatalogAsync());
        var entries = await Task.WhenAll(tasks);
        foreach (var entry in entries.Where(entry => entry is not null))
        {
            _recommendedReleases[entry!.Kind] = RecommendedRuntimeReleaseSelector.Select(
                entry.Kind,
                entry.Releases);
        }

        ApplyRecommendedVersionOptions();
    }

    private async Task<IReadOnlyList<string>> RefreshRecommendedVersionsAsync(bool forceRefresh)
    {
        IsCatalogLoading = true;
        var tasks = _providers.Select(async provider =>
        {
            try
            {
                var available = forceRefresh && provider is ICachedRuntimeProvider cachedProvider
                    ? await cachedProvider.RefreshAvailableAsync()
                    : await provider.GetAvailableAsync();
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

        try
        {
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
        finally
        {
            IsCatalogLoading = false;
        }
    }

    private async Task RefreshTasksAsync()
    {
        var operations = await _state.GetOperationsAsync();
        Replace(Tasks, operations.Select(item => new TaskRow(
            item.StartedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            GetTaskStatusText(item.Status),
            GetTaskStatusBrush(item.Status),
            GetTaskName(item.Name),
            GetTaskNameBrush(item.Name),
            item.Kind is null ? "-" : $"{GetRuntimeDisplayName(item.Kind.Value)}@{item.Version}")));
    }

    private void ApplyRuntimeFilter()
    {
        Replace(ManagedRuntimes, _allManagedRuntimes.Where(item => item.RuntimeKind == SelectedRuntimeKind));
        Replace(ExternalRuntimes, _allExternalRuntimes.Where(item => item.RuntimeKind == SelectedRuntimeKind));
        var installed = _allManagedRuntimes
            .Where(item => item.RuntimeKind == SelectedRuntimeKind && !item.IsDeleted)
            .Select(item => CreateInstalledRuntimeRow(item, isManaged: true))
            .Concat(_allExternalRuntimes
                .Where(item => item.RuntimeKind == SelectedRuntimeKind)
                .Select(item => CreateInstalledRuntimeRow(item, isManaged: false)));
        Replace(InstalledRuntimes, installed);
        OnPropertyChanged(nameof(InstalledRuntimesEmptyVisibility));
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
        ApplyVersionRows();
    }

    private void ApplyVersionRows()
    {
        var releases = _recommendedReleases.GetValueOrDefault(SelectedRuntimeKind, []);
        var managed = _allManagedRuntimes
            .Where(item => item.RuntimeKind == SelectedRuntimeKind && !item.IsDeleted)
            .ToArray();
        var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rows = new List<RuntimeVersionRow>();

        foreach (var release in releases)
        {
            included.Add(release.Version);
            rows.Add(CreateVersionRow(
                release.Version,
                managed.FirstOrDefault(item => string.Equals(
                    item.Version,
                    release.Version,
                    StringComparison.OrdinalIgnoreCase)),
                release.DownloadUri,
                release.ReleasePageUri));
        }

        rows.AddRange(managed
            .Where(item => included.Add(item.Version))
            .Select(item => CreateVersionRow(
                item.Version,
                item,
                downloadUri: null,
                releasePageUri: null)));

        Replace(VersionRows, rows);
        OnPropertyChanged(nameof(CatalogLoadingVisibility));
        OnPropertyChanged(nameof(VersionRowsEmptyVisibility));
    }

    private RuntimeVersionRow CreateVersionRow(
        string version,
        RuntimeRow? managed,
        Uri? downloadUri,
        Uri? releasePageUri)
    {
        var line = GetReleaseLine(SelectedRuntimeKind, version);
        var releaseLine = SelectedRuntimeKind switch
        {
            RuntimeKind.Node => $"Node.js {line} LTS",
            RuntimeKind.Java => $"Temurin JDK {line} LTS",
            RuntimeKind.Python => $"Python {line} 稳定版",
            _ => GetRuntimeDisplayName(SelectedRuntimeKind),
        };
        var state = managed switch
        {
            { IsCurrent: true } => T("当前全局", "Current global"),
            not null => T("已安装", "Installed"),
            _ => T("未安装", "Not installed"),
        };

        var feedback = GetRuntimeFeedback(new RuntimeTarget(SelectedRuntimeKind, version));
        return new RuntimeVersionRow(
            SelectedRuntimeKind,
            releaseLine,
            version,
            state,
            managed is not null,
            managed?.IsCurrent == true,
            managed?.IsDeleted == true,
            T("安装", "Install"),
            T("卸载", "Uninstall"),
            downloadUri?.AbsoluteUri,
            releasePageUri?.AbsoluteUri,
            feedback);
    }

    private InstalledRuntimeRow CreateInstalledRuntimeRow(RuntimeRow item, bool isManaged)
    {
        var feedback = GetRuntimeFeedback(new RuntimeTarget(item.RuntimeKind, item.Version));
        return new InstalledRuntimeRow(
            item.RuntimeKind,
            item.Version,
            item.Path,
            isManaged,
            item.IsCurrent,
            item.IsCurrent
                ? T("取消当前版本", "Clear current version")
                : T("设为当前版本", "Set current version"),
            T("卸载", "Uninstall"),
            T("复制路径", "Copy path"),
            feedback);
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

    private async Task RunBusyAsync(
        Func<Task> action,
        bool showSuccessOrFailureDialog = true,
        Action<Exception>? onError = null)
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
            try
            {
                await RefreshTasksAsync();
            }
            catch
            {
                // Preserve the original operation error if task-history refresh also fails.
            }

            onError?.Invoke(exception);
            if (showSuccessOrFailureDialog)
            {
                NotifyUser(T("操作失败", "Operation failed"), exception.Message, isError: true);
            }
        }
        finally
        {
            IsBusy = false;
            NotifyCommands();
        }
    }

    private bool CanRun() => !IsBusy;
    private bool CanInstall() => !IsBusy && SelectedRecommendedVersion is { IsManaged: false };
    private bool CanUseSelected() => !IsBusy && SelectedRuntime is { IsDeleted: false, IsCurrent: false };
    private bool CanUninstallSelected() => !IsBusy && SelectedRuntime is { IsDeleted: false, IsCurrent: false };
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
        SaveModulePreferencesCommand.NotifyCanExecuteChanged();
    }

    private string GetTaskStatusText(OperationStatus status) => status switch
    {
        OperationStatus.Running => T("进行中", "Running"),
        OperationStatus.Succeeded => T("成功", "Succeeded"),
        OperationStatus.Failed => T("失败", "Failed"),
        OperationStatus.Cancelled => T("已取消", "Cancelled"),
        _ => status.ToString(),
    };

    private static Brush GetTaskStatusBrush(OperationStatus status) => status switch
    {
        OperationStatus.Succeeded => new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
        OperationStatus.Failed => new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
        OperationStatus.Running => new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray),
    };

    private string GetTaskName(string name) => name.ToLowerInvariant() switch
    {
        "install" => T("安装", "Install"),
        "uninstall" => T("卸载", "Uninstall"),
        "restore" => T("恢复（历史）", "Restore (history)"),
        _ => name,
    };

    private static Brush GetTaskNameBrush(string name) => name.ToLowerInvariant() switch
    {
        "install" => new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
        "uninstall" => new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray),
    };

    private string T(string chinese, string english) => IsEnglish ? english : chinese;

    private void NotifyUser(string title, string message, bool isError) =>
        NotificationRequested?.Invoke(new UserNotification(title, message, isError));

    private void NotifyLocalizedProperties()
    {
        string[] properties =
        [
            nameof(TaskHistoryText), nameof(SettingsText), nameof(RefreshText),
            nameof(InstalledTabText),
            nameof(VersionManagementTabText), nameof(NoInstalledText), nameof(NoVersionsText),
            nameof(CatalogLoadingText),
            nameof(VersionHeaderText), nameof(PathHeaderText),
            nameof(EnvironmentHeaderText), nameof(ReleaseLineHeaderText), nameof(IsInstalledHeaderText),
            nameof(OperationHeaderText), nameof(TimeHeaderText),
            nameof(StatusHeaderText), nameof(TaskTypeHeaderText), nameof(TargetHeaderText),
            nameof(ModulesText), nameof(SaveModulesText), nameof(LanguageText),
            nameof(RuntimeInstallDescription),
        ];
        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }
    }

    private void SetRuntimeProgress(RuntimeTarget target, OperationProgress progress)
    {
        if (!_installingTargets.Contains(target))
        {
            return;
        }

        var previousPercentage = GetRuntimeFeedback(target)?.Percentage ?? 0;
        var percentage = progress.Percentage is { } value
            ? Math.Max(previousPercentage, Math.Clamp(value, 0, 100))
            : previousPercentage;
        var message = IsEnglish
            ? progress.Stage.ToLowerInvariant() switch
            {
                "prepare" => "Preparing…",
                "resolve" => "Resolving version…",
                "download" => "Downloading…",
                "extract" => "Extracting…",
                "health" => "Checking runtime…",
                "commit" => "Saving runtime…",
                "state" => "Saving state…",
                "current" => "Updating global version…",
                "complete" => "Complete",
                _ => progress.Stage,
            }
            : progress.Detail ?? progress.Stage;
        SetRuntimeFeedback(target, new RuntimeOperationFeedback(
            percentage,
            message,
            RuntimeFeedbackKind.Running,
            true));
    }

    private RuntimeOperationFeedback? GetRuntimeFeedback(RuntimeTarget target) =>
        _runtimeFeedback.GetValueOrDefault(target);

    private void SetRuntimeFeedback(RuntimeTarget target, RuntimeOperationFeedback feedback)
    {
        _runtimeFeedback[target] = feedback;
        foreach (var row in VersionRows.Where(row =>
                     row.RuntimeKind == target.Kind
                     && string.Equals(row.Version, target.Version, StringComparison.OrdinalIgnoreCase)))
        {
            row.UpdateFeedback(feedback);
        }

        foreach (var row in InstalledRuntimes.Where(row =>
                     row.RuntimeKind == target.Kind
                     && string.Equals(row.Version, target.Version, StringComparison.OrdinalIgnoreCase)))
        {
            row.UpdateFeedback(feedback);
        }
    }

    private void ClearRuntimeFeedback(RuntimeTarget target)
    {
        _runtimeFeedback.Remove(target);
        foreach (var row in VersionRows.Where(row =>
                     row.RuntimeKind == target.Kind
                     && string.Equals(row.Version, target.Version, StringComparison.OrdinalIgnoreCase)))
        {
            row.UpdateFeedback(null);
        }

        foreach (var row in InstalledRuntimes.Where(row =>
                     row.RuntimeKind == target.Kind
                     && string.Equals(row.Version, target.Version, StringComparison.OrdinalIgnoreCase)))
        {
            row.UpdateFeedback(null);
        }
    }

    private RuntimeModulePreferences CreateModulePreferences(string? language = null) => new(
        IsModuleEnabled(RuntimeKind.Node),
        IsModuleEnabled(RuntimeKind.Java),
        IsModuleEnabled(RuntimeKind.Python),
        language ?? SelectedLanguage?.Code ?? "zh-CN",
        GetOrderedModuleKinds());

    private void ReplaceModuleSettings(RuntimeModulePreferences preferences)
    {
        foreach (var setting in ModuleSettings)
        {
            setting.PropertyChanged -= OnModuleSettingPropertyChanged;
        }

        ModuleSettings.Clear();
        foreach (var kind in preferences.GetModuleOrder())
        {
            var setting = new RuntimeModuleSetting(
                kind,
                GetRuntimeDisplayName(kind),
                GetRuntimeIconPath(kind),
                preferences.IsEnabled(kind));
            setting.PropertyChanged += OnModuleSettingPropertyChanged;
            ModuleSettings.Add(setting);
        }

        NotifyModuleVisibilityChanged();
    }

    private static string GetRuntimeIconPath(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => "ms-appx:///Assets/RuntimeIcons/nodejs.svg",
        RuntimeKind.Java => "ms-appx:///Assets/RuntimeIcons/java.svg",
        RuntimeKind.Python => "ms-appx:///Assets/RuntimeIcons/python.svg",
        _ => string.Empty,
    };

    private void OnModuleSettingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RuntimeModuleSetting.IsEnabled))
        {
            ModuleSaveStatusText = string.Empty;
            NotifyModuleVisibilityChanged();
        }
    }

    private void NotifyModuleVisibilityChanged()
    {
        OnPropertyChanged(nameof(NodeModuleEnabled));
        OnPropertyChanged(nameof(JavaModuleEnabled));
        OnPropertyChanged(nameof(PythonModuleEnabled));
        OnPropertyChanged(nameof(NodeModuleVisibility));
        OnPropertyChanged(nameof(JavaModuleVisibility));
        OnPropertyChanged(nameof(PythonModuleVisibility));
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

public sealed class RuntimeVersionRow : ObservableObject
{
    private RuntimeOperationFeedback? _feedback;

    public RuntimeVersionRow(
        RuntimeKind runtimeKind,
        string releaseLine,
        string version,
        string state,
        bool isManaged,
        bool isCurrent,
        bool isDeleted,
        string installText,
        string uninstallText,
        string? downloadUrl,
        string? releasePageUrl,
        RuntimeOperationFeedback? feedback)
    {
        RuntimeKind = runtimeKind;
        ReleaseLine = releaseLine;
        Version = version;
        State = state;
        IsManaged = isManaged;
        IsCurrent = isCurrent;
        IsDeleted = isDeleted;
        InstallText = installText;
        UninstallText = uninstallText;
        DownloadUrl = downloadUrl;
        ReleasePageUrl = releasePageUrl;
        _feedback = feedback;
    }

    public RuntimeKind RuntimeKind { get; }
    public string ReleaseLine { get; }
    public string Version { get; }
    public string State { get; }
    public bool IsManaged { get; }
    public bool IsCurrent { get; }
    public bool IsDeleted { get; }
    public string InstallText { get; }
    public string UninstallText { get; }
    public string? DownloadUrl { get; }
    public string? ReleasePageUrl { get; }
    public bool CanInstall => !IsManaged && _feedback?.IsActive != true;
    public bool CanUse => IsManaged && !IsCurrent && !IsDeleted;
    public bool CanClearGlobal => IsCurrent && !IsDeleted;
    public bool CanUninstall => IsManaged && !IsCurrent && !IsDeleted;
    public Visibility InstalledVisibility => IsManaged && !IsDeleted ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstallVisibility => CanInstall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UseVisibility => CanUse ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClearGlobalVisibility => CanClearGlobal ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UninstallVisibility => CanUninstall ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VersionLinkVisibility => DownloadUrl is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility VersionTextVisibility => DownloadUrl is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReleasePageLinkVisibility => ReleasePageUrl is not null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ReleasePageTextVisibility => ReleasePageUrl is null ? Visibility.Visible : Visibility.Collapsed;
    public double OperationPercentage => _feedback?.Percentage ?? 0;
    public string OperationStatusText => _feedback?.Message ?? string.Empty;
    public Brush OperationStatusBrush => RuntimeFeedbackBrushes.Get(_feedback?.Kind);
    public Visibility ProgressVisibility => _feedback?.IsActive == true ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FeedbackVisibility => string.IsNullOrWhiteSpace(_feedback?.Message)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public void UpdateFeedback(RuntimeOperationFeedback? feedback)
    {
        _feedback = feedback;
        OnPropertyChanged(nameof(OperationPercentage));
        OnPropertyChanged(nameof(OperationStatusText));
        OnPropertyChanged(nameof(OperationStatusBrush));
        OnPropertyChanged(nameof(ProgressVisibility));
        OnPropertyChanged(nameof(FeedbackVisibility));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallVisibility));
    }
}

public sealed class InstalledRuntimeRow : ObservableObject
{
    private RuntimeOperationFeedback? _feedback;
    private string _pathStatusText = string.Empty;

    public InstalledRuntimeRow(
        RuntimeKind runtimeKind,
        string version,
        string path,
        bool isManaged,
        bool isCurrent,
        string environmentActionName,
        string uninstallText,
        string copyPathToolTip,
        RuntimeOperationFeedback? feedback)
    {
        RuntimeKind = runtimeKind;
        Version = version;
        Path = path;
        IsManaged = isManaged;
        IsCurrent = isCurrent;
        EnvironmentActionName = environmentActionName;
        UninstallText = uninstallText;
        CopyPathToolTip = copyPathToolTip;
        _feedback = feedback;
    }

    public RuntimeKind RuntimeKind { get; }
    public string Version { get; }
    public string Path { get; }
    public bool IsManaged { get; }
    public bool IsCurrent { get; }
    public string EnvironmentActionName { get; }
    public string UninstallText { get; }
    public string CopyPathToolTip { get; }
    public bool CanToggleEnvironment => IsManaged;
    public bool CanUninstall => IsManaged && !IsCurrent;
    public Visibility SetEnvironmentVisibility => IsManaged && !IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClearEnvironmentVisibility => IsManaged && IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UninstallVisibility => CanUninstall ? Visibility.Visible : Visibility.Collapsed;
    public string OperationStatusText => _feedback?.Message ?? string.Empty;
    public Brush OperationStatusBrush => RuntimeFeedbackBrushes.Get(_feedback?.Kind);
    public Visibility FeedbackVisibility => string.IsNullOrWhiteSpace(_feedback?.Message)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public string PathStatusText => _pathStatusText;
    public Visibility PathStatusVisibility => string.IsNullOrWhiteSpace(_pathStatusText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public void UpdateFeedback(RuntimeOperationFeedback? feedback)
    {
        _feedback = feedback;
        OnPropertyChanged(nameof(OperationStatusText));
        OnPropertyChanged(nameof(OperationStatusBrush));
        OnPropertyChanged(nameof(FeedbackVisibility));
    }

    public void SetPathStatus(string text)
    {
        _pathStatusText = text;
        OnPropertyChanged(nameof(PathStatusText));
        OnPropertyChanged(nameof(PathStatusVisibility));
    }
}

public sealed record RuntimeOperationFeedback(
    double Percentage,
    string Message,
    RuntimeFeedbackKind Kind,
    bool IsActive);

public enum RuntimeFeedbackKind
{
    Running,
    Success,
    Error,
}

internal static class RuntimeFeedbackBrushes
{
    public static Brush Get(RuntimeFeedbackKind? kind) => kind switch
    {
        RuntimeFeedbackKind.Success => new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
        RuntimeFeedbackKind.Error => new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
        RuntimeFeedbackKind.Running => new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray),
    };
}

public sealed partial class RuntimeModuleSetting : ObservableObject
{
    public RuntimeModuleSetting(RuntimeKind kind, string displayName, string iconPath, bool isEnabled)
    {
        Kind = kind;
        DisplayName = displayName;
        IconPath = iconPath;
        IsEnabled = isEnabled;
    }

    public RuntimeKind Kind { get; }
    public string DisplayName { get; }
    public string IconPath { get; }

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }
}

public sealed record TaskRow(
    string StartedAt,
    string Status,
    Brush StatusBrush,
    string Name,
    Brush NameBrush,
    string Target);

public sealed record LanguageOption(string Code, string DisplayName);

public sealed record UserNotification(string Title, string Message, bool IsError);

internal sealed record RuntimeCatalogResult(
    RuntimeKind Kind,
    IReadOnlyList<RuntimeRelease>? Releases,
    string? Error);
