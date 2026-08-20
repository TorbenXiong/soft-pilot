using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SoftPilot.Application;
using SoftPilot.Application.Abstractions;
using SoftPilot.Domain;
using SoftPilot.Infrastructure.Providers;

namespace SoftPilot.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly IReadOnlyList<IExternalRuntimeDetector> _detectors;
    private readonly IReadOnlyList<IRuntimeProvider> _providers;
    private readonly IStateStore _state;
    private readonly IOperationCoordinator _operations;
    private readonly IGlobalRuntimeService _global;
    private readonly IRuntimeModulePreferencesStore _modulePreferences;
    private readonly IRedisServiceManager _redisServices;
    private readonly IMySqlServiceManager _mySqlServices;
    private readonly IGitService _gitBash;
    private IReadOnlyList<RuntimeRow> _allManagedRuntimes = [];
    private IReadOnlyList<RuntimeRow> _allExternalRuntimes = [];
    private readonly Dictionary<RuntimeKind, IReadOnlyList<RuntimeRelease>> _recommendedReleases = [];
    private readonly Dictionary<RuntimeTarget, RuntimeOperationFeedback> _runtimeFeedback = [];
    private readonly HashSet<RuntimeTarget> _installingTargets = [];
    private readonly SemaphoreSlim _modulePreferencesSaveGate = new(1, 1);
    private int _moduleSaveStatusGeneration;
    private bool _modulePreferencesLoaded;
    private bool _redisServiceStatusAvailable;
    private string? _runningRedisVersion;
    private string? _redisServiceProblem;
    private bool _mySqlServiceStatusAvailable;
    private IReadOnlyDictionary<string, MySqlServiceStatus> _mySqlServiceStatuses =
        new Dictionary<string, MySqlServiceStatus>(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<RuntimeTarget> _serviceOperationTargets = [];
    private GitRelease? _latestGitBashRelease;
    private string? _gitBashLocalProblem;
    private string? _gitBashRemoteProblem;
    private string? _gitBashOperationProblem;
    private string? _gitBashConfigurationProblem;

    public MainViewModel(
        IEnumerable<IExternalRuntimeDetector> detectors,
        IEnumerable<IRuntimeProvider> providers,
        IStateStore state,
        IOperationCoordinator operations,
        IGlobalRuntimeService global,
        IRuntimeModulePreferencesStore modulePreferences,
        IRedisServiceManager redisServices,
        IMySqlServiceManager mySqlServices,
        IGitService gitBash)
    {
        _detectors = detectors.ToArray();
        _providers = providers.ToArray();
        _state = state;
        _operations = operations;
        _global = global;
        _modulePreferences = modulePreferences;
        _redisServices = redisServices;
        _mySqlServices = mySqlServices;
        _gitBash = gitBash;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, CanRun);
        InstallCommand = new AsyncRelayCommand(InstallAsync, CanInstall);
        UseSelectedCommand = new AsyncRelayCommand(UseSelectedAsync, CanUseSelected);
        UninstallSelectedCommand = new AsyncRelayCommand(UninstallSelectedAsync, CanUninstallSelected);
        LanguageOptions =
        [
            new LanguageOption("en-US", "English"),
            new LanguageOption("zh-CN", "简体中文"),
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
    public ObservableCollection<GitEnvironmentCheckRow> GitEnvironmentChecks { get; } = [];
    public IReadOnlyList<LanguageOption> LanguageOptions { get; }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IAsyncRelayCommand InstallCommand { get; }
    public IAsyncRelayCommand UseSelectedCommand { get; }
    public IAsyncRelayCommand UninstallSelectedCommand { get; }

    public event Action<UserNotification>? NotificationRequested;
    public event Action? ModulePreferencesChanged;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RuntimeDisplayName))]
    [NotifyPropertyChangedFor(nameof(RuntimeInstallDescription))]
    [NotifyPropertyChangedFor(nameof(ServiceColumnWidth))]
    [NotifyPropertyChangedFor(nameof(ServiceColumnVisibility))]
    [NotifyPropertyChangedFor(nameof(PortColumnWidth))]
    [NotifyPropertyChangedFor(nameof(PortColumnVisibility))]
    public partial RuntimeKind SelectedRuntimeKind { get; private set; } = RuntimeKind.Node;

    [ObservableProperty]
    public partial RuntimeVersionOption? SelectedRecommendedVersion { get; set; }

    [ObservableProperty]
    public partial string RecommendedVersionHint { get; set; } = "Loading the official version catalog…";

    public bool NodeModuleEnabled => IsModuleEnabled(RuntimeKind.Node);
    public bool JavaModuleEnabled => IsModuleEnabled(RuntimeKind.Java);
    public bool PythonModuleEnabled => IsModuleEnabled(RuntimeKind.Python);
    public bool RedisModuleEnabled => IsModuleEnabled(RuntimeKind.Redis);
    public bool MySqlModuleEnabled => IsModuleEnabled(RuntimeKind.MySql);
    public bool GitModuleEnabled => IsModuleEnabled(ModuleKind.Git);

    [ObservableProperty]
    public partial LanguageOption? SelectedLanguage { get; private set; }

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial bool IsGitBashOperating { get; private set; }

    [ObservableProperty]
    public partial bool GitBashIsInstalled { get; private set; }

    [ObservableProperty]
    public partial string GitBashInstalledVersion { get; private set; } = "—";

    [ObservableProperty]
    public partial string GitBashLatestVersion { get; private set; } = "—";

    [ObservableProperty]
    public partial string GitBashProblemText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial string GitBashOperationText { get; private set; } = string.Empty;

    [ObservableProperty]
    public partial double GitBashOperationPercentage { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitBashPathStatusVisibility))]
    public partial string GitBashPathStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GitUserName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GitUserEmail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsGitConfigurationSaving { get; private set; }

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
    public Visibility RedisModuleVisibility => RedisModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MySqlModuleVisibility => MySqlModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitModuleVisibility => GitModuleEnabled ? Visibility.Visible : Visibility.Collapsed;
    public GridLength ServiceColumnWidth => IsServiceRuntime(SelectedRuntimeKind)
        ? new GridLength(110)
        : new GridLength(0);
    public Visibility ServiceColumnVisibility => IsServiceRuntime(SelectedRuntimeKind)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public GridLength PortColumnWidth => IsServiceRuntime(SelectedRuntimeKind)
        ? new GridLength(140)
        : new GridLength(0);
    public Visibility PortColumnVisibility => IsServiceRuntime(SelectedRuntimeKind)
        ? Visibility.Visible
        : Visibility.Collapsed;
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
    public string EnvironmentHeaderText => T("终端默认版本", "Terminal default");
    public string ReleaseLineHeaderText => T("版本线", "Release line");
    public string IsInstalledHeaderText => T("是否安装", "Installed");
    public string ServiceHeaderText => T("服务", "Service");
    public string PortHeaderText => T("端口", "Port");
    public string OperationHeaderText => T("操作", "Action");
    public string TimeHeaderText => T("时间", "Time");
    public string StatusHeaderText => T("状态", "Status");
    public string TaskTypeHeaderText => T("类型", "Type");
    public string TargetHeaderText => T("目标", "Target");
    public string ModulesText => T("模块", "Modules");
    public string ModuleAutoSaveText => T("更改会自动保存", "Changes are saved automatically");
    public string LanguageText => T("语言", "Language");
    public string StartRedisText => T("启动", "Start");
    public string StopRedisText => T("停止", "Stop");
    public string GitBashText => "Git";
    public string GitBashInstalledVersionLabel => T("已安装版本", "Installed version");
    public string GitBashLatestVersionLabel => T("官方最新版本", "Latest official version");
    public string GitBashInstallPathLabel => T("安装路径", "Install path");
    public string GitBashInstallPath => _gitBash.InstallDirectory;
    public string GitBashLauncherPath => _gitBash.LauncherPath;
    public string GitBashPrimaryActionText => GitBashIsInstalled ? T("升级", "Upgrade") : T("安装", "Install");
    public string GitBashLaunchText => T("启动 Git Bash", "Launch Git Bash");
    public string GitBashLaunchAsAdministratorText => T("启动 Git Bash(管理员)", "Run Git Bash as administrator");
    public string GitBashUninstallText => T("卸载", "Uninstall");
    public string GitCopyPathToolTip => T("复制安装路径", "Copy installation path");
    public string GitBashConfigurationTitle => T("常用配置", "Common configuration");
    public string GitUserNameLabel => T("用户名（user.name）", "User name (user.name)");
    public string GitUserEmailLabel => T("邮箱（user.email）", "Email (user.email)");
    public string GitUserNamePlaceholder => T("例如：张三", "For example: Jane Doe");
    public string GitUserEmailPlaceholder => T("例如：name@example.com", "For example: name@example.com");
    public string GitConfigurationSaveText => T("保存配置", "Save configuration");
    public string GitConfigurationScopeText => T(
        "保存后写入当前 Windows 用户的全局 Git 配置（~/.gitconfig）。留空会删除对应配置项。",
        "Saves to the current Windows user's global Git configuration (~/.gitconfig). Leaving a field empty removes that setting.");
    public string GitEnvironmentTitle => T("Git 组件", "Git components");
    public string GitCheckItemHeader => T("组件", "Component");
    public string GitCheckStatusHeader => T("状态", "Status");
    public string GitCheckResultHeader => T("版本或结果", "Version or result");
    public string GitBashReleasePageUrl => _latestGitBashRelease?.ReleasePageUri.AbsoluteUri ?? string.Empty;
    public string GitBashDownloadUrl => _latestGitBashRelease?.DownloadUri.AbsoluteUri ?? string.Empty;
    public bool GitBashUpdateAvailable => GitBashIsInstalled
        && _latestGitBashRelease is not null
        && !string.Equals(GitBashInstalledVersion, _latestGitBashRelease.Version, StringComparison.OrdinalIgnoreCase);
    public Visibility GitBashPrimaryActionVisibility => !GitBashIsInstalled || GitBashUpdateAvailable
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility GitBashInstalledActionsVisibility => GitBashIsInstalled
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility GitBashOperationVisibility => string.IsNullOrWhiteSpace(GitBashOperationText)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility GitBashProblemVisibility => string.IsNullOrWhiteSpace(GitBashProblemText)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility GitBashProgressVisibility => IsGitBashOperating ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitBashReleasePageVisibility => _latestGitBashRelease is not null
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility GitBashPathStatusVisibility => string.IsNullOrWhiteSpace(GitBashPathStatusText)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public bool CanRunGitBashAction => !IsBusy && !IsGitBashOperating && _latestGitBashRelease is not null;
    public bool CanUseInstalledGitBash => GitBashIsInstalled && !IsBusy && !IsGitBashOperating;
    public bool CanEditGitConfiguration => GitBashIsInstalled
        && !IsBusy
        && !IsGitBashOperating
        && !IsGitConfigurationSaving;
    public Visibility GitEnvironmentChecksVisibility => GitBashIsInstalled
        ? Visibility.Visible
        : Visibility.Collapsed;

    public string RuntimeDisplayName => SelectedRuntimeKind switch
    {
        RuntimeKind.Node => "Node.js",
        RuntimeKind.Java => "Java",
        RuntimeKind.Python => "Python",
        RuntimeKind.Redis => "Redis",
        RuntimeKind.MySql => "MySQL",
        _ => SelectedRuntimeKind.ToString(),
    };

    public string RuntimeInstallDescription => SelectedRuntimeKind switch
    {
        RuntimeKind.Node => T(
            "从官方目录推荐最近两个 LTS 主版本线的最新稳定补丁。",
            "Recommends the latest stable patch from each of the two most recent LTS lines in the official catalog."),
        RuntimeKind.Java => T(
            "从 Eclipse Temurin 官方目录推荐各个 LTS 版本线的最新稳定 JDK。",
            "Recommends the latest stable JDK from each LTS line in the official Eclipse Temurin catalog."),
        RuntimeKind.Python => T(
            "Python 没有 LTS；这里推荐仍在近期支持范围内的稳定分支最新补丁。",
            "Python has no LTS releases; this lists the latest patch from each recently supported stable branch."),
        RuntimeKind.Redis => T(
            "版本来自 Redis 官方发布目录，Windows x64 归档由 redis-windows 社区项目构建，仅建议用于本地开发。",
            "Versions are cross-checked with official Redis releases. Windows x64 archives are community builds from redis-windows and are intended for local development."),
        RuntimeKind.MySql => T(
            "提供 Oracle 官方 Windows x64 ZIP：MySQL 8.4 LTS 为推荐线，5.7.44 仅用于兼容旧项目。缺少兼容的 Visual C++ x64 Runtime 时会验证 Microsoft 签名并请求管理员授权安装。",
            "Provides official Oracle Windows x64 ZIP archives. MySQL 8.4 LTS is recommended; 5.7.44 is retained only for legacy compatibility. If needed, a Microsoft-signed Visual C++ x64 Runtime is installed with administrator approval."),
        _ => T("从官方目录选择推荐版本。", "Select a recommended version from the official catalog."),
    };

    public bool IsModuleEnabled(RuntimeKind kind) => IsModuleEnabled(ToModuleKind(kind));

    public bool IsModuleEnabled(ModuleKind kind) =>
        ModuleSettings.FirstOrDefault(item => item.Kind == kind)?.IsEnabled == true;

    public IReadOnlyList<ModuleKind> GetOrderedModuleKinds() =>
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
        await RefreshServiceStatusesAsync();
        await RefreshGitBashLocalStatusAsync();
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
                await RefreshServiceStatusesAsync();
                await RefreshGitBashLocalStatusAsync();
                var remoteWarnings = await RefreshRemoteDataAsync(forceCatalogRefresh: true);
                var gitBashWarning = await RefreshGitBashLatestAsync();
                await RefreshTasksAsync();
                var warnings = remoteWarnings
                    .Prepend(preferencesWarning)
                    .Prepend(gitBashWarning)
                    .OfType<string>()
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
        var remoteWarnings = await RefreshRemoteDataAsync(forceCatalogRefresh: false);
        var gitBashWarning = await RefreshGitBashLatestAsync();
        var warnings = remoteWarnings
            .Prepend(preferencesWarning)
            .Prepend(gitBashWarning)
            .OfType<string>()
            .ToArray();
        if (warnings.Length > 0 && _recommendedReleases.Count == 0)
        {
            NotifyUser(T("加载未完全成功", "Loading incomplete"), string.Join(" ", warnings), isError: true);
        }
    }

    private async Task RefreshGitBashLocalStatusAsync()
    {
        var status = await _gitBash.GetInstalledStatusAsync();
        GitBashIsInstalled = status.IsInstalled;
        GitBashInstalledVersion = status.Version ?? "—";
        _gitBashLocalProblem = status.Problem;
        IReadOnlyList<GitEnvironmentCheck> checks = status.IsInstalled
            ? await _gitBash.GetEnvironmentChecksAsync()
            : [];
        Replace(
            GitEnvironmentChecks,
            checks.Select(check => new GitEnvironmentCheckRow(
                GetGitEnvironmentCheckName(check.Name),
                check.IsAvailable,
                check.IsAvailable ? T("正常", "OK") : T("缺失", "Missing"),
                check.IsAvailable
                    ? new SolidColorBrush(Microsoft.UI.Colors.ForestGreen)
                    : new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
                check.Result)));
        if (status.IsInstalled)
        {
            try
            {
                var configuration = await _gitBash.GetGlobalConfigurationAsync();
                GitUserName = configuration.UserName;
                GitUserEmail = configuration.UserEmail;
                _gitBashConfigurationProblem = null;
            }
            catch (Exception exception)
            {
                _gitBashConfigurationProblem = T(
                    $"读取配置失败：{GetDetailedExceptionMessage(exception)}",
                    $"Unable to read configuration: {GetDetailedExceptionMessage(exception)}");
            }
        }
        else
        {
            GitUserName = string.Empty;
            GitUserEmail = string.Empty;
            _gitBashConfigurationProblem = null;
        }

        UpdateGitBashProblemText();
        NotifyGitBashProperties();
    }

    private async Task<string?> RefreshGitBashLatestAsync()
    {
        try
        {
            _latestGitBashRelease = await _gitBash.GetLatestReleaseAsync();
            GitBashLatestVersion = _latestGitBashRelease.Version;
            _gitBashRemoteProblem = null;
            UpdateGitBashProblemText();
            NotifyGitBashProperties();
            return null;
        }
        catch (Exception exception)
        {
            _latestGitBashRelease = null;
            GitBashLatestVersion = T("加载失败", "Unavailable");
            _gitBashRemoteProblem = T(
                $"Git 最新版本加载失败：{exception.Message}",
                $"Unable to load the latest Git release: {exception.Message}");
            UpdateGitBashProblemText();
            NotifyGitBashProperties();
            return _gitBashRemoteProblem;
        }
    }

    public async Task InstallOrUpgradeGitBashAsync()
    {
        if (!CanRunGitBashAction)
        {
            return;
        }

        IsGitBashOperating = true;
        _gitBashOperationProblem = null;
        UpdateGitBashProblemText();
        GitBashOperationPercentage = 0;
        GitBashOperationText = GitBashIsInstalled
            ? T("等待升级…", "Waiting to upgrade…")
            : T("等待安装…", "Waiting to install…");
        NotifyGitBashProperties();
        try
        {
            var progress = new Progress<OperationProgress>(value =>
            {
                GitBashOperationPercentage = value.Percentage ?? GitBashOperationPercentage;
                GitBashOperationText = GetGitBashProgressText(value);
                NotifyGitBashProperties();
            });
            await _gitBash.InstallOrUpgradeLatestAsync(progress);
            await RefreshGitBashLocalStatusAsync();
            GitBashOperationPercentage = 0;
            GitBashOperationText = string.Empty;
        }
        catch (Exception exception)
        {
            _gitBashOperationProblem = GetDetailedExceptionMessage(exception);
            UpdateGitBashProblemText();
            GitBashOperationText = T("操作失败", "Operation failed");
        }
        finally
        {
            IsGitBashOperating = false;
            await RefreshTasksAsync();
            NotifyGitBashProperties();
        }
    }

    public async Task SaveGitConfigurationAsync()
    {
        if (!CanEditGitConfiguration)
        {
            return;
        }

        IsGitConfigurationSaving = true;
        NotifyCommands();
        NotifyGitBashProperties();
        try
        {
            await _gitBash.SaveGlobalConfigurationAsync(new GitGlobalConfiguration(
                GitUserName,
                GitUserEmail));
            var configuration = await _gitBash.GetGlobalConfigurationAsync();
            GitUserName = configuration.UserName;
            GitUserEmail = configuration.UserEmail;
            _gitBashConfigurationProblem = null;
            UpdateGitBashProblemText();
            NotifyUser(
                T("保存成功", "Saved"),
                T("Git 全局配置已保存。", "The global Git configuration was saved."),
                isError: false,
                autoDismiss: true);
        }
        catch (Exception exception)
        {
            var detail = GetDetailedExceptionMessage(exception);
            NotifyUser(T("Git 配置保存失败", "Unable to save Git configuration"), detail, isError: true);
        }
        finally
        {
            IsGitConfigurationSaving = false;
            NotifyCommands();
            NotifyGitBashProperties();
        }
    }

    public async Task UninstallGitBashAsync()
    {
        if (!CanUseInstalledGitBash)
        {
            return;
        }

        IsGitBashOperating = true;
        _gitBashOperationProblem = null;
        UpdateGitBashProblemText();
        GitBashOperationText = T("正在卸载…", "Uninstalling…");
        NotifyGitBashProperties();
        try
        {
            await _gitBash.UninstallAsync();
            await RefreshGitBashLocalStatusAsync();
            GitBashOperationText = T("Git 已卸载", "Git was uninstalled");
        }
        catch (Exception exception)
        {
            _gitBashOperationProblem = exception.Message;
            UpdateGitBashProblemText();
            GitBashOperationText = T("卸载失败", "Uninstall failed");
        }
        finally
        {
            IsGitBashOperating = false;
            await RefreshTasksAsync();
            NotifyGitBashProperties();
        }
    }

    private async Task<IReadOnlyList<string>> RefreshRemoteDataAsync(bool forceCatalogRefresh)
    {
        var catalogTask = RefreshRecommendedVersionsAsync(forceCatalogRefresh);
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
        return catalogWarnings
            .Prepend(externalWarning)
            .OfType<string>()
            .ToArray();
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

    public async Task ChangeLanguageAsync(LanguageOption option)
    {
        if (SelectedLanguage?.Code == option.Code)
        {
            return;
        }

        SelectedLanguage = option;
        NotifyLocalizedProperties();
        ApplyRuntimeFilter();
        await RefreshGitBashLocalStatusAsync();
        await RefreshTasksAsync();

        try
        {
            await _modulePreferencesSaveGate.WaitAsync();
            try
            {
                await _modulePreferences.SaveAsync(CreateModulePreferences(option.Code));
            }
            finally
            {
                _modulePreferencesSaveGate.Release();
            }
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
        await RunBusyAsync(async () =>
        {
            SetRuntimeFeedback(target, new RuntimeOperationFeedback(
                0,
                T("正在设置终端默认版本…", "Setting terminal default…"),
                RuntimeFeedbackKind.Running,
                true,
                RuntimeFeedbackPlacement.Environment));
            await _global.UseAsync(kind, version);
            SetTransientRuntimeFeedback(target, new RuntimeOperationFeedback(
                100,
                T("已设为终端默认版本", "Terminal default set"),
                RuntimeFeedbackKind.Success,
                false,
                RuntimeFeedbackPlacement.Environment));
            await RefreshCoreAsync();
        }, showSuccessOrFailureDialog: false,
        onError: exception => SetRuntimeFeedback(target, new RuntimeOperationFeedback(
            0,
            T($"设置失败：{exception.Message}", $"Update failed: {exception.Message}"),
            RuntimeFeedbackKind.Error,
            false,
            RuntimeFeedbackPlacement.Environment)));
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
                SetTransientRuntimeFeedback(target.Value, new RuntimeOperationFeedback(
                    100,
                    T("已取消终端默认版本", "Terminal default cleared"),
                    RuntimeFeedbackKind.Success,
                    false,
                    RuntimeFeedbackPlacement.Environment));
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
                    false,
                    RuntimeFeedbackPlacement.Environment));
            }
        });
    }

    public Task<MySqlCredentials> GetMySqlCredentialsAsync(string version) =>
        _mySqlServices.GetCredentialsAsync(version);

    public async Task SaveMySqlPortAsync(InstalledRuntimeRow row)
    {
        if (IsBusy || _serviceOperationTargets.Contains(new RuntimeTarget(row.RuntimeKind, row.Version))
            || !row.TryGetEditedPort(out var port))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            await _mySqlServices.SetConfiguredPortAsync(row.Version, port);
            row.CommitPort(port);
            NotifyUser(
                T("端口已保存", "Port saved"),
                T($"MySQL {row.Version} 将使用端口 {port}", $"MySQL {row.Version} will use port {port}"),
                isError: false,
                autoDismiss: true);
        }, showSuccessOrFailureDialog: false,
        onError: exception => NotifyUser(
            T("端口保存失败", "Unable to save port"),
            exception.Message,
            isError: true));
    }

    private Task UninstallSelectedAsync() =>
        UninstallRuntimeAsync(SelectedRuntime!.RuntimeKind, SelectedRuntime.Version);

    public Task UninstallVersionAsync(RuntimeVersionRow row, bool deleteData = false) =>
        row.CanUninstall ? UninstallRuntimeAsync(row.RuntimeKind, row.Version, deleteData) : Task.CompletedTask;

    public Task UninstallInstalledRuntimeAsync(InstalledRuntimeRow row, bool deleteData = false) =>
        row.CanUninstall ? UninstallRuntimeAsync(row.RuntimeKind, row.Version, deleteData) : Task.CompletedTask;

    private async Task UninstallRuntimeAsync(RuntimeKind kind, string version, bool deleteData = false)
    {
        var target = new RuntimeTarget(kind, version);
        ClearRuntimeFeedback(target);
        await RunBusyAsync(async () =>
        {
            await _operations.UninstallAsync(target, new RuntimeUninstallOptions(deleteData));
            await RefreshCoreAsync();
        }, showSuccessOrFailureDialog: false,
        onError: exception => NotifyUser(
            T("卸载失败", "Uninstall failed"),
            exception.Message,
            isError: true));
    }

    private async Task RefreshCoreAsync()
    {
        await RefreshRuntimeDataAsync(includeExternal: false);
        await RefreshServiceStatusesAsync();
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
                    T(
                        $"{GetRuntimeDisplayName(provider.Kind)} 官方版本目录加载失败：{exception.Message}",
                        $"Unable to load the official {GetRuntimeDisplayName(provider.Kind)} version catalog: {exception.Message}"));
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
            IsGitOperation(item)
                ? $"Git@{item.Version ?? "-"}"
                : item.Kind is null
                    ? "-"
                    : $"{GetRuntimeDisplayName(item.Kind.Value)}@{RuntimeVersionDisplayFormatter.Format(item.Kind.Value, item.Version ?? "-")}")));
    }

    private void ApplyRuntimeFilter()
    {
        Replace(ManagedRuntimes, _allManagedRuntimes
            .Where(item => item.RuntimeKind == SelectedRuntimeKind)
            .OrderByDescending(item => item.Version, RuntimeVersionComparer.Instance));
        Replace(ExternalRuntimes, _allExternalRuntimes
            .Where(item => item.RuntimeKind == SelectedRuntimeKind)
            .OrderByDescending(item => item.Version, RuntimeVersionComparer.Instance));
        var installed = _allManagedRuntimes
            .Where(item => item.RuntimeKind == SelectedRuntimeKind && !item.IsDeleted)
            .Select(item => CreateInstalledRuntimeRow(item, isManaged: true))
            .Concat(_allExternalRuntimes
                .Where(item => item.RuntimeKind == SelectedRuntimeKind)
                .Select(item => CreateInstalledRuntimeRow(item, isManaged: false)))
            .OrderByDescending(item => item.Version, RuntimeVersionComparer.Instance);
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
            ? T(
                "未加载到推荐版本。请检查网络和官方目录后点击右上角“刷新”。",
                "No recommended versions were loaded. Check the network and official catalog, then select Refresh in the upper-right corner.")
            : SelectedRuntimeKind switch
            {
                RuntimeKind.Node => T(
                    "仅显示最近两个 Node.js LTS 主版本线，每条版本线自动选择最新稳定补丁。",
                    "Shows the two most recent Node.js LTS lines and selects the latest stable patch from each."),
                RuntimeKind.Java => T(
                    "仅显示 Eclipse Temurin LTS 版本线，每条版本线自动选择最新稳定 JDK。",
                    "Shows Eclipse Temurin LTS lines and selects the latest stable JDK from each."),
                RuntimeKind.Python => T(
                    "Python 没有 LTS；仅显示最新五个稳定分支，每条分支自动选择最新补丁。",
                    "Python has no LTS releases; this shows the five newest stable branches and selects the latest patch from each."),
                RuntimeKind.Redis => T(
                    "显示每个可验证 Redis 主版本线的最新稳定补丁；Windows 归档来自 redis-windows 社区构建并校验 GitHub SHA-256。",
                    "Shows the latest stable patch for every verifiable Redis major line. Windows archives are redis-windows community builds verified with GitHub SHA-256 digests."),
                RuntimeKind.MySql => T(
                    "推荐 MySQL 8.4 LTS；5.7.44 已停止常规支持，仅用于无法升级的旧项目。官方归档会验证 OpenPGP 签名。",
                    "MySQL 8.4 LTS is recommended. 5.7.44 is out of regular support and is offered only for legacy projects. Official archives are verified with OpenPGP."),
                _ => T("已从官方目录筛选推荐版本。", "Recommended versions were selected from the official catalog."),
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
            RuntimeKind.Python => T($"Python {line} 稳定版", $"Python {line} stable"),
            RuntimeKind.Redis => T($"Redis {line} 稳定版", $"Redis {line} stable"),
            RuntimeKind.MySql => line == "8.4"
                ? "MySQL 8.4 LTS"
                : T("MySQL 5.7 兼容版（已停止常规支持）", "MySQL 5.7 legacy (regular support ended)"),
            _ => GetRuntimeDisplayName(SelectedRuntimeKind),
        };
        var state = managed switch
        {
            { IsCurrent: true } => T("终端默认", "Terminal default"),
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
        var servicePort = item.RuntimeKind switch
        {
            RuntimeKind.MySql when isManaged => _mySqlServices.GetConfiguredPort(item.Version),
            RuntimeKind.MySql => 3306,
            RuntimeKind.Redis => 6379,
            _ => 0,
        };
        var row = new InstalledRuntimeRow(
            item.RuntimeKind,
            item.Version,
            item.Path,
            isManaged,
            item.IsCurrent,
            item.IsCurrent
                ? T("取消终端默认版本", "Clear terminal default")
                : T("设为终端默认版本", "Set as terminal default"),
            GetEnvironmentActionToolTip(item.RuntimeKind, item.IsCurrent),
            T("卸载", "Uninstall"),
            T("复制路径", "Copy path"),
            T($"复制 MySQL {item.Version} 的 root 密码", $"Copy the root password for MySQL {item.Version}"),
            servicePort,
            T("保存端口", "Save port"),
            feedback);
        UpdateServiceRow(row);
        return row;
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
            RuntimeKind.Java => $"JDK {line} LTS — {RuntimeVersionDisplayFormatter.Format(release.Kind, release.Version)}",
            RuntimeKind.Python => T($"Python {line} 稳定版 — {release.Version}", $"Python {line} stable — {release.Version}"),
            RuntimeKind.Redis => T($"Redis {line} 稳定版 — {release.Version}", $"Redis {line} stable — {release.Version}"),
            RuntimeKind.MySql => line == "8.4"
                ? $"MySQL 8.4 LTS — {release.Version}"
                : T($"MySQL 5.7 兼容版 — {release.Version}", $"MySQL 5.7 legacy — {release.Version}"),
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
        UpdateServiceRows();
        NotifyGitBashProperties();
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
            UpdateServiceRows();
            NotifyGitBashProperties();
        }
    }

    private bool CanRun() => !IsBusy && !IsGitConfigurationSaving;
    private bool CanInstall() => !IsBusy && SelectedRecommendedVersion is { IsManaged: false };
    private bool CanUseSelected() => !IsBusy && SelectedRuntime is { IsDeleted: false, IsCurrent: false };
    private bool CanUninstallSelected() => !IsBusy && SelectedRuntime is { IsDeleted: false, IsCurrent: false };

    public async Task StartInstalledServiceAsync(InstalledRuntimeRow row)
    {
        var target = new RuntimeTarget(row.RuntimeKind, row.Version);
        if (IsBusy || !row.CanStartService || !_serviceOperationTargets.Add(target))
        {
            return;
        }

        UpdateServiceRows();
        try
        {
            try
            {
                if (row.RuntimeKind == RuntimeKind.MySql)
                {
                    await _mySqlServices.StartAsync(row.Version);
                }
                else
                {
                    await _redisServices.StartAsync(row.Version);
                }
            }
            catch (Exception exception)
            {
                NotifyUser(
                row.RuntimeKind == RuntimeKind.MySql
                    ? T("MySQL 启动失败", "Unable to start MySQL")
                    : T("Redis 启动失败", "Unable to start Redis"),
                exception.Message,
                isError: true);
            }
        }
        finally
        {
            _serviceOperationTargets.Remove(target);
            await RefreshServiceStatusesAsync();
            UpdateServiceRows();
        }
    }

    public async Task StopInstalledServiceAsync(InstalledRuntimeRow row)
    {
        var target = new RuntimeTarget(row.RuntimeKind, row.Version);
        if (IsBusy || !row.CanStopService || !_serviceOperationTargets.Add(target))
        {
            return;
        }

        UpdateServiceRows();
        try
        {
            try
            {
                if (row.RuntimeKind == RuntimeKind.MySql)
                {
                    await _mySqlServices.StopAsync(row.Version);
                }
                else
                {
                    await _redisServices.StopAsync();
                }
            }
            catch (Exception exception)
            {
                NotifyUser(
                row.RuntimeKind == RuntimeKind.MySql
                    ? T("MySQL 停止失败", "Unable to stop MySQL")
                    : T("Redis 停止失败", "Unable to stop Redis"),
                exception.Message,
                isError: true);
            }
        }
        finally
        {
            _serviceOperationTargets.Remove(target);
            await RefreshServiceStatusesAsync();
            UpdateServiceRows();
        }
    }

    private async Task RefreshServiceStatusesAsync()
    {
        try
        {
            var status = await _redisServices.GetStatusAsync();
            _redisServiceStatusAvailable = true;
            _runningRedisVersion = status.IsRunning ? status.Version : null;
            _redisServiceProblem = status.Problem;
        }
        catch (Exception exception)
        {
            _redisServiceStatusAvailable = false;
            _runningRedisVersion = null;
            _redisServiceProblem = exception.Message;
        }

        try
        {
            var statuses = await _mySqlServices.GetStatusesAsync();
            _mySqlServiceStatusAvailable = true;
            _mySqlServiceStatuses = statuses
                .Where(status => status.Version is not null)
                .ToDictionary(status => status.Version!, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception exception)
        {
            _mySqlServiceStatusAvailable = false;
            _mySqlServiceStatuses = new Dictionary<string, MySqlServiceStatus>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = new MySqlServiceStatus(false, Problem: exception.Message),
            };
        }
        UpdateServiceRows();
    }

    private void UpdateServiceRows()
    {
        foreach (var row in InstalledRuntimes)
        {
            UpdateServiceRow(row);
        }
    }

    private void UpdateServiceRow(InstalledRuntimeRow row)
    {
        if (row.RuntimeKind == RuntimeKind.MySql)
        {
            _mySqlServiceStatuses.TryGetValue(row.Version, out var status);
            var operationInProgress = _serviceOperationTargets.Contains(
                new RuntimeTarget(RuntimeKind.MySql, row.Version));
            row.UpdateServiceState(
                _mySqlServiceStatusAvailable && status is not null,
                status?.IsRunning == true,
                IsBusy || operationInProgress,
                operationInProgress,
                status?.Problem ?? (_mySqlServiceStatuses.TryGetValue(string.Empty, out var unavailable) ? unavailable.Problem : null),
                StartRedisText,
                StopRedisText,
                row.IsManaged
                    ? T("MySQL 服务状态不可用", "MySQL service status unavailable")
                    : T("外部 MySQL 仅支持只读发现", "External MySQL installations are read-only"));
            return;
        }

        row.UpdateServiceState(
            _redisServiceStatusAvailable,
            string.Equals(row.Version, _runningRedisVersion, StringComparison.OrdinalIgnoreCase),
            IsBusy || _serviceOperationTargets.Any(target => target.Kind == RuntimeKind.Redis),
            _serviceOperationTargets.Contains(new RuntimeTarget(RuntimeKind.Redis, row.Version)),
            _redisServiceProblem,
            StartRedisText,
            StopRedisText,
            row.IsManaged
                ? T("Redis 服务状态不可用", "Redis service status unavailable")
                : T("外部 Redis 仅支持只读发现", "External Redis installations are read-only"));
    }

    partial void OnSelectedRuntimeKindChanged(RuntimeKind value)
    {
        ApplyRuntimeFilter();
    }

    partial void OnSelectedRecommendedVersionChanged(RuntimeVersionOption? value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnSelectedRuntimeChanged(RuntimeRow? value) => NotifyCommands();

    private static string GetRuntimeDisplayName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => "Node.js",
        RuntimeKind.Java => "Java",
        RuntimeKind.Python => "Python",
        RuntimeKind.Redis => "Redis",
        RuntimeKind.MySql => "MySQL",
        _ => kind.ToString(),
    };

    private static bool IsServiceRuntime(RuntimeKind kind) =>
        kind is RuntimeKind.Redis or RuntimeKind.MySql;

    private static string GetReleaseLine(RuntimeKind kind, string version)
    {
        var parts = version.Split('.');
        return kind is RuntimeKind.Python or RuntimeKind.MySql && parts.Length >= 2
            ? $"{parts[0]}.{parts[1]}"
            : parts[0];
    }

    private void NotifyCommands()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        InstallCommand.NotifyCanExecuteChanged();
        UseSelectedCommand.NotifyCanExecuteChanged();
        UninstallSelectedCommand.NotifyCanExecuteChanged();
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
        "upgrade" => T("升级", "Upgrade"),
        "uninstall" => T("卸载", "Uninstall"),
        "git-install" or "git-bash-install" => T("安装", "Install"),
        "git-upgrade" or "git-bash-upgrade" => T("升级", "Upgrade"),
        "git-uninstall" or "git-bash-uninstall" => T("卸载", "Uninstall"),
        "restore" => T("恢复（历史）", "Restore (history)"),
        _ => name,
    };

    private static Brush GetTaskNameBrush(string name) => name.ToLowerInvariant() switch
    {
        "install" or "upgrade" or "git-install" or "git-upgrade" or "git-bash-install" or "git-bash-upgrade"
            => new SolidColorBrush(Microsoft.UI.Colors.ForestGreen),
        "uninstall" or "git-uninstall" or "git-bash-uninstall"
            => new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
        _ => new SolidColorBrush(Microsoft.UI.Colors.Gray),
    };

    private static bool IsGitOperation(OperationRecord operation) =>
        operation.Kind is null
        && (operation.Name is "install" or "upgrade" or "uninstall"
            || operation.Name.StartsWith("git-", StringComparison.OrdinalIgnoreCase));

    private string GetEnvironmentActionToolTip(RuntimeKind kind, bool isCurrent)
    {
        if (isCurrent)
        {
            return T(
                "取消此运行时的当前版本选择，并移除终端环境中的 SoftPilot 运行时入口；不会卸载该版本。",
                "Clear the current-version selection for this runtime and remove its SoftPilot terminal entry. The version will not be uninstalled.");
        }

        return kind switch
        {
            RuntimeKind.Node => T(
                "将 current\\node 指向此版本，并更新用户终端环境，使新打开的终端使用此版本的 node、npm 和 npx；不会重新安装或删除版本。",
                "Point current\\node to this version and update the user terminal environment so newly opened terminals use its node, npm, and npx. No version is reinstalled or removed."),
            RuntimeKind.Java => T(
                "将 current\\java 指向此版本，并更新 JAVA_HOME，使新打开的终端使用此 JDK；不会重新安装或删除版本。",
                "Point current\\java to this version and update JAVA_HOME so newly opened terminals use this JDK. No version is reinstalled or removed."),
            RuntimeKind.Python => T(
                "将 current\\python 指向此版本，使 SoftPilot 的 Python 命令入口使用它；不会设置 PYTHONHOME，也不会重新安装或删除版本。",
                "Point current\\python to this version so SoftPilot's Python command entry uses it. PYTHONHOME is not set, and no version is reinstalled or removed."),
            RuntimeKind.Redis => T(
                "将 current\\redis 指向此版本，使 redis-server 和 redis-cli 命令入口使用它；服务启动和停止在已安装版本的“服务”列中单独管理。",
                "Point current\\redis to this version for the redis-server and redis-cli command entries. Start and stop the service separately from the Service column in Installed versions."),
            RuntimeKind.MySql => T(
                "将 current\\mysql 指向此版本，使 mysqld、mysql 和 mysqladmin 命令入口使用它；服务启动和停止在“服务”列中单独管理。",
                "Point current\\mysql to this version for the mysqld, mysql, and mysqladmin command entries. Start and stop the service separately from the Service column."),
            _ => T(
                "将此版本设为 SoftPilot 当前使用的版本；不会重新安装或删除版本。",
                "Set this as the version currently used by SoftPilot. No version is reinstalled or removed."),
        };
    }

    private string T(string chinese, string english) => IsEnglish ? english : chinese;

    private string GetGitBashProgressText(OperationProgress progress) => progress.Stage.ToLowerInvariant() switch
    {
        "download" => T("正在下载并校验 Git…", "Downloading and verifying Git…"),
        "download-retry" => T(progress.Detail ?? "下载连接失败，正在重试…", "Download connection failed. Retrying…"),
        "extract" => T("正在解包 Git…", "Extracting Git…"),
        "health" => T("正在核对实际版本…", "Verifying the installed version…"),
        "commit" => T("正在提交安装目录…", "Committing the installation…"),
        "complete" => string.Empty,
        _ => progress.Detail ?? T("正在处理…", "Working…"),
    };

    private static string GetGitEnvironmentCheckName(string name) => name;

    private void NotifyGitBashProperties()
    {
        string[] properties =
        [
            nameof(GitBashPrimaryActionText), nameof(GitBashUpdateAvailable),
            nameof(GitBashPrimaryActionVisibility),
            nameof(GitBashInstalledActionsVisibility), nameof(GitBashOperationVisibility),
            nameof(GitBashProblemVisibility), nameof(GitBashProgressVisibility),
            nameof(GitBashReleasePageUrl), nameof(GitBashDownloadUrl), nameof(GitBashReleasePageVisibility),
            nameof(GitBashPathStatusVisibility),
            nameof(CanRunGitBashAction), nameof(CanUseInstalledGitBash),
            nameof(CanEditGitConfiguration), nameof(GitEnvironmentChecksVisibility),
        ];
        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }
    }

    private void UpdateGitBashProblemText()
    {
        GitBashProblemText = string.Join(
            Environment.NewLine,
            new[] { _gitBashLocalProblem, _gitBashRemoteProblem, _gitBashOperationProblem }
                .Append(_gitBashConfigurationProblem)
                .Where(problem => !string.IsNullOrWhiteSpace(problem)));
    }

    private void NotifyUser(string title, string message, bool isError, bool autoDismiss = false) =>
        NotificationRequested?.Invoke(new UserNotification(title, message, isError, autoDismiss));

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
            nameof(ServiceHeaderText), nameof(OperationHeaderText), nameof(TimeHeaderText),
            nameof(StatusHeaderText), nameof(TaskTypeHeaderText), nameof(TargetHeaderText),
            nameof(ModulesText), nameof(ModuleAutoSaveText), nameof(LanguageText),
            nameof(StartRedisText), nameof(StopRedisText),
            nameof(RuntimeInstallDescription),
            nameof(GitBashInstalledVersionLabel),
            nameof(GitBashLatestVersionLabel), nameof(GitBashInstallPathLabel),
            nameof(GitBashPrimaryActionText), nameof(GitBashLaunchText),
            nameof(GitBashLaunchAsAdministratorText), nameof(GitBashUninstallText), nameof(GitCopyPathToolTip),
            nameof(GitBashConfigurationTitle),
            nameof(GitUserNameLabel), nameof(GitUserEmailLabel),
            nameof(GitUserNamePlaceholder), nameof(GitUserEmailPlaceholder),
            nameof(GitConfigurationSaveText), nameof(GitConfigurationScopeText),
            nameof(GitEnvironmentTitle), nameof(GitCheckItemHeader),
            nameof(GitCheckStatusHeader), nameof(GitCheckResultHeader),
        ];
        foreach (var property in properties)
        {
            OnPropertyChanged(property);
        }

        NotifyGitBashProperties();
    }

    private static string GetDetailedExceptionMessage(Exception exception)
    {
        var messages = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message)
                && !messages.Contains(current.Message, StringComparer.Ordinal))
            {
                messages.Add(current.Message.Trim());
            }
        }

        return string.Join(" → ", messages);
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
        var message = string.Equals(progress.Stage, "download", StringComparison.OrdinalIgnoreCase)
            ? FormatDownloadProgress(progress.Detail)
            : IsEnglish
            ? progress.Stage.ToLowerInvariant() switch
            {
                "prepare" => "Preparing…",
                "resolve" => "Resolving version…",
                "manager" => "Preparing Python Install Manager…",
                "prerequisite-check" => "Checking Microsoft Visual C++ Runtime…",
                "prerequisite-download" => "Downloading Microsoft Visual C++ Runtime…",
                "prerequisite-verify" => "Verifying Microsoft installer signature…",
                "prerequisite-install" => "Installing Microsoft Visual C++ Runtime…",
                "download" => "Downloading…",
                "source" => progress.Detail ?? "Selecting download source…",
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

    private string FormatDownloadProgress(string? detail)
    {
        if (Uri.TryCreate(detail, UriKind.Absolute, out var source))
        {
            return T($"正在从 {source.Host} 下载…", $"Downloading from {source.Host}…");
        }

        return T(detail ?? "正在下载…", "Downloading…");
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

    private void SetTransientRuntimeFeedback(RuntimeTarget target, RuntimeOperationFeedback feedback)
    {
        SetRuntimeFeedback(target, feedback);
        _ = ClearRuntimeFeedbackAfterDelayAsync(target, feedback);
    }

    private async Task ClearRuntimeFeedbackAfterDelayAsync(
        RuntimeTarget target,
        RuntimeOperationFeedback expectedFeedback)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (Equals(GetRuntimeFeedback(target), expectedFeedback))
        {
            ClearRuntimeFeedback(target);
        }
    }

    private RuntimeModulePreferences CreateModulePreferences(string? language = null) => new(
        IsModuleEnabled(RuntimeKind.Node),
        IsModuleEnabled(RuntimeKind.Java),
        IsModuleEnabled(RuntimeKind.Python),
        language ?? SelectedLanguage?.Code ?? "en-US",
        GetOrderedModuleKinds(),
        IsModuleEnabled(RuntimeKind.Redis),
        IsModuleEnabled(RuntimeKind.MySql),
        IsModuleEnabled(ModuleKind.Git));

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
                GetModuleDisplayName(kind),
                GetModuleIconPath(kind),
                preferences.IsEnabled(kind));
            setting.PropertyChanged += OnModuleSettingPropertyChanged;
            ModuleSettings.Add(setting);
        }

        NotifyModuleVisibilityChanged();
    }

    private static string GetModuleDisplayName(ModuleKind kind) => kind switch
    {
        ModuleKind.Node => "Node.js",
        ModuleKind.Java => "Java",
        ModuleKind.Python => "Python",
        ModuleKind.Redis => "Redis",
        ModuleKind.MySql => "MySQL",
        ModuleKind.Git => "Git",
        _ => kind.ToString(),
    };

    private static string GetModuleIconPath(ModuleKind kind) => kind switch
    {
        ModuleKind.Node => "ms-appx:///Assets/RuntimeIcons/nodejs.svg",
        ModuleKind.Java => "ms-appx:///Assets/RuntimeIcons/java.svg",
        ModuleKind.Python => "ms-appx:///Assets/RuntimeIcons/python.svg",
        ModuleKind.Redis => "ms-appx:///Assets/RuntimeIcons/redis.svg",
        ModuleKind.MySql => "ms-appx:///Assets/RuntimeIcons/mysql.svg",
        ModuleKind.Git => "ms-appx:///Assets/RuntimeIcons/git.svg",
        _ => string.Empty,
    };

    private static ModuleKind ToModuleKind(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => ModuleKind.Node,
        RuntimeKind.Java => ModuleKind.Java,
        RuntimeKind.Python => ModuleKind.Python,
        RuntimeKind.Redis => ModuleKind.Redis,
        RuntimeKind.MySql => ModuleKind.MySql,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private void OnModuleSettingPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(RuntimeModuleSetting.IsEnabled))
        {
            NotifyModuleVisibilityChanged();
            ModulePreferencesChanged?.Invoke();
            QueueModulePreferencesSave();
        }
    }

    public void ModuleOrderChanged()
    {
        ModulePreferencesChanged?.Invoke();
        QueueModulePreferencesSave();
    }

    private void QueueModulePreferencesSave()
    {
        if (!_modulePreferencesLoaded)
        {
            return;
        }

        _ = SaveModulePreferencesAsync();
    }

    private async Task SaveModulePreferencesAsync()
    {
        await _modulePreferencesSaveGate.WaitAsync();
        try
        {
            var generation = ++_moduleSaveStatusGeneration;
            ModuleSaveStatusBrush = new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);
            ModuleSaveStatusText = T("正在保存…", "Saving…");
            await _modulePreferences.SaveAsync(CreateModulePreferences());
            ModuleSaveStatusBrush = new SolidColorBrush(Microsoft.UI.Colors.ForestGreen);
            ModuleSaveStatusText = T("已保存", "Saved");
            _ = ClearModuleSaveStatusAfterDelayAsync(generation);
        }
        catch (Exception exception)
        {
            _moduleSaveStatusGeneration++;
            ModuleSaveStatusBrush = new SolidColorBrush(Microsoft.UI.Colors.Firebrick);
            ModuleSaveStatusText = T($"保存失败：{exception.Message}", $"Unable to save: {exception.Message}");
        }
        finally
        {
            _modulePreferencesSaveGate.Release();
        }
    }

    private async Task ClearModuleSaveStatusAfterDelayAsync(int generation)
    {
        await Task.Delay(TimeSpan.FromSeconds(3));
        if (generation == _moduleSaveStatusGeneration)
        {
            ModuleSaveStatusText = string.Empty;
        }
    }

    private void NotifyModuleVisibilityChanged()
    {
        OnPropertyChanged(nameof(NodeModuleEnabled));
        OnPropertyChanged(nameof(JavaModuleEnabled));
        OnPropertyChanged(nameof(PythonModuleEnabled));
        OnPropertyChanged(nameof(RedisModuleEnabled));
        OnPropertyChanged(nameof(MySqlModuleEnabled));
        OnPropertyChanged(nameof(GitModuleEnabled));
        OnPropertyChanged(nameof(NodeModuleVisibility));
        OnPropertyChanged(nameof(JavaModuleVisibility));
        OnPropertyChanged(nameof(PythonModuleVisibility));
        OnPropertyChanged(nameof(RedisModuleVisibility));
        OnPropertyChanged(nameof(MySqlModuleVisibility));
        OnPropertyChanged(nameof(GitModuleVisibility));
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
    bool IsDeleted)
{
    public string DisplayVersion => RuntimeVersionDisplayFormatter.Format(RuntimeKind, Version);
    public string VersionToolTip => string.Equals(DisplayVersion, Version, StringComparison.Ordinal)
        ? DisplayVersion
        : $"{DisplayVersion} · {Version}";
}

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
    public string DisplayVersion => RuntimeVersionDisplayFormatter.Format(RuntimeKind, Version);
    public string VersionToolTip => string.Equals(DisplayVersion, Version, StringComparison.Ordinal)
        ? DisplayVersion
        : $"{DisplayVersion} · {Version}";
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
    private bool _serviceStatusAvailable;
    private bool _isServiceRunning;
    private bool _isServiceControlBusy;
    private bool _isServiceOperationInProgress;
    private string? _serviceProblem;
    private string _startServiceText = "Start";
    private string _stopServiceText = "Stop";
    private string _serviceUnavailableText = "Service status unavailable";
    private int _configuredPort;
    private string _portText;

    public InstalledRuntimeRow(
        RuntimeKind runtimeKind,
        string version,
        string path,
        bool isManaged,
        bool isCurrent,
        string environmentActionName,
        string environmentActionToolTip,
        string uninstallText,
        string copyPathToolTip,
        string copyMySqlPasswordToolTip,
        int servicePort,
        string savePortText,
        RuntimeOperationFeedback? feedback)
    {
        RuntimeKind = runtimeKind;
        Version = version;
        Path = path;
        IsManaged = isManaged;
        IsCurrent = isCurrent;
        EnvironmentActionName = environmentActionName;
        EnvironmentActionToolTip = environmentActionToolTip;
        UninstallText = uninstallText;
        CopyPathToolTip = copyPathToolTip;
        CopyMySqlPasswordToolTip = copyMySqlPasswordToolTip;
        _configuredPort = servicePort;
        _portText = servicePort > 0 ? servicePort.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        SavePortText = savePortText;
        _feedback = feedback;
    }

    public RuntimeKind RuntimeKind { get; }
    public string Version { get; }
    public string DisplayVersion => RuntimeVersionDisplayFormatter.Format(RuntimeKind, Version);
    public string VersionToolTip => string.Equals(DisplayVersion, Version, StringComparison.Ordinal)
        ? DisplayVersion
        : $"{DisplayVersion} · {Version}";
    public string Path { get; }
    public bool IsManaged { get; }
    public bool IsCurrent { get; }
    public string EnvironmentActionName { get; }
    public string EnvironmentActionToolTip { get; }
    public string UninstallText { get; }
    public string CopyPathToolTip { get; }
    public string CopyMySqlPasswordToolTip { get; }
    public string SavePortText { get; }
    private bool IsServiceRuntime => RuntimeKind is RuntimeKind.Redis or RuntimeKind.MySql;
    private string ServiceName => RuntimeKind == RuntimeKind.MySql ? "MySQL" : "Redis";
    public GridLength ServiceColumnWidth => IsServiceRuntime
        ? new GridLength(110)
        : new GridLength(0);
    public Visibility ServiceColumnVisibility => IsServiceRuntime
        ? Visibility.Visible
        : Visibility.Collapsed;
    public bool CanStartService => IsServiceRuntime
        && IsManaged
        && _serviceStatusAvailable
        && !_isServiceRunning;
    public bool CanStopService => IsServiceRuntime
        && IsManaged
        && _serviceStatusAvailable
        && _isServiceRunning;
    public bool IsServiceActionEnabled => !_isServiceControlBusy;
    public Visibility StartServiceVisibility => CanStartService && !_isServiceOperationInProgress
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility StopServiceVisibility => CanStopService && !_isServiceOperationInProgress
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ServiceProgressVisibility => IsServiceRuntime && IsManaged && _isServiceOperationInProgress
        ? Visibility.Visible
        : Visibility.Collapsed;
    public GridLength PortColumnWidth => IsServiceRuntime ? new GridLength(140) : new GridLength(0);
    public Visibility PortColumnVisibility => IsServiceRuntime ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MySqlPortEditorVisibility => RuntimeKind == RuntimeKind.MySql && IsManaged
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility FixedPortVisibility => IsServiceRuntime && (RuntimeKind != RuntimeKind.MySql || !IsManaged)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public string PortText
    {
        get => _portText;
        set
        {
            if (SetProperty(ref _portText, value))
            {
                OnPropertyChanged(nameof(CanSavePort));
            }
        }
    }
    public bool CanEditPort => RuntimeKind == RuntimeKind.MySql
        && IsManaged
        && !_isServiceRunning
        && !_isServiceControlBusy;
    public bool CanSavePort => CanEditPort
        && int.TryParse(_portText, out var port)
        && port is >= 1 and <= 65535
        && port != _configuredPort;
    public Visibility ServiceUnavailableVisibility => IsServiceRuntime
        && (!IsManaged || !_serviceStatusAvailable)
        && !_isServiceOperationInProgress
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility CopyMySqlPasswordVisibility => RuntimeKind == RuntimeKind.MySql && IsManaged
        ? Visibility.Visible
        : Visibility.Collapsed;
    public bool CanCopyMySqlPassword => RuntimeKind == RuntimeKind.MySql && IsManaged && !_isServiceControlBusy;
    public string StartServiceToolTip => _serviceProblem is null
        ? $"{_startServiceText} {ServiceName} {Version}"
        : $"{_startServiceText} {ServiceName} {Version} · {_serviceProblem}";
    public string StopServiceToolTip => $"{_stopServiceText} {ServiceName} {Version}";
    public string ServiceUnavailableToolTip => IsManaged && !string.IsNullOrWhiteSpace(_serviceProblem)
        ? $"{_serviceUnavailableText}: {_serviceProblem}"
        : _serviceUnavailableText;
    public bool CanToggleEnvironment => IsManaged;
    public bool CanUninstall => IsManaged && !IsCurrent;
    public Visibility SetEnvironmentVisibility => IsManaged && !IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ClearEnvironmentVisibility => IsManaged && IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UninstallVisibility => CanUninstall ? Visibility.Visible : Visibility.Collapsed;
    public string OperationStatusText => _feedback?.Message ?? string.Empty;
    public Brush OperationStatusBrush => RuntimeFeedbackBrushes.Get(_feedback?.Kind);
    public Visibility EnvironmentFeedbackVisibility => string.IsNullOrWhiteSpace(_feedback?.Message)
        || _feedback?.Placement != RuntimeFeedbackPlacement.Environment
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility OperationFeedbackVisibility => string.IsNullOrWhiteSpace(_feedback?.Message)
        || _feedback?.Placement == RuntimeFeedbackPlacement.Environment
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
        OnPropertyChanged(nameof(EnvironmentFeedbackVisibility));
        OnPropertyChanged(nameof(OperationFeedbackVisibility));
    }

    public void SetPathStatus(string text)
    {
        _pathStatusText = text;
        OnPropertyChanged(nameof(PathStatusText));
        OnPropertyChanged(nameof(PathStatusVisibility));
    }

    public bool TryGetEditedPort(out int port) =>
        int.TryParse(_portText, out port) && port is >= 1 and <= 65535;

    public void CommitPort(int port)
    {
        _configuredPort = port;
        PortText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    public void UpdateServiceState(
        bool statusAvailable,
        bool isRunning,
        bool isControlBusy,
        bool isOperationInProgress,
        string? problem,
        string startText,
        string stopText,
        string unavailableText)
    {
        _serviceStatusAvailable = statusAvailable;
        _isServiceRunning = isRunning;
        _isServiceControlBusy = isControlBusy;
        _isServiceOperationInProgress = isOperationInProgress;
        _serviceProblem = problem;
        _startServiceText = startText;
        _stopServiceText = stopText;
        _serviceUnavailableText = unavailableText;
        OnPropertyChanged(nameof(CanStartService));
        OnPropertyChanged(nameof(CanStopService));
        OnPropertyChanged(nameof(IsServiceActionEnabled));
        OnPropertyChanged(nameof(StartServiceVisibility));
        OnPropertyChanged(nameof(StopServiceVisibility));
        OnPropertyChanged(nameof(ServiceProgressVisibility));
        OnPropertyChanged(nameof(ServiceUnavailableVisibility));
        OnPropertyChanged(nameof(CanCopyMySqlPassword));
        OnPropertyChanged(nameof(CanEditPort));
        OnPropertyChanged(nameof(CanSavePort));
        OnPropertyChanged(nameof(StartServiceToolTip));
        OnPropertyChanged(nameof(StopServiceToolTip));
        OnPropertyChanged(nameof(ServiceUnavailableToolTip));
    }
}

public sealed record RuntimeOperationFeedback(
    double Percentage,
    string Message,
    RuntimeFeedbackKind Kind,
    bool IsActive,
    RuntimeFeedbackPlacement Placement = RuntimeFeedbackPlacement.Operation);

public enum RuntimeFeedbackPlacement
{
    Operation,
    Environment,
}

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
    public RuntimeModuleSetting(ModuleKind kind, string displayName, string iconPath, bool isEnabled)
    {
        Kind = kind;
        DisplayName = displayName;
        IconPath = iconPath;
        IsEnabled = isEnabled;
    }

    public ModuleKind Kind { get; }
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

public sealed record GitEnvironmentCheckRow(
    string Name,
    bool IsAvailable,
    string Status,
    Brush StatusBrush,
    string Result);

public sealed record UserNotification(
    string Title,
    string Message,
    bool IsError,
    bool AutoDismiss = false);

internal sealed record RuntimeCatalogResult(
    RuntimeKind Kind,
    IReadOnlyList<RuntimeRelease>? Releases,
    string? Error);
