using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SoftPilot.Application;
using SoftPilot.Domain;
using SoftPilot.Gui.Controls;
using SoftPilot.Gui.ViewModels;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.System;
using Windows.UI.Core;
using Windows.Graphics;

namespace SoftPilot.Gui;

public sealed partial class MainWindow : Window
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private readonly SemaphoreSlim _jsonTransformGate = new(1, 1);
    private readonly DispatcherTimer _jsonPreviewTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private readonly Popup _copySuccessPopup;
    private readonly Border _copySuccessPopupSurface;
    private readonly FontIcon _copySuccessPopupIcon;
    private readonly TextBlock _copySuccessPopupText;
    private CancellationTokenSource? _transientNotificationCancellation;
    private CancellationTokenSource? _copySuccessCancellation;
    private string _currentTag = "runtime:node";
    private Guid? _currentJsonHistoryId;
    private JsonFormattingMode _jsonFormattingMode = JsonFormattingMode.Beautified;
    private EnvironmentVariableScope _environmentVariableScope = EnvironmentVariableScope.User;
    private Grid? _activeEnvironmentEditRow;
    private Button? _activeEnvironmentValueButton;
    private Button? _activeEnvironmentSaveButton;
    private Button? _activeEnvironmentCancelButton;
    private TextBox? _activeEnvironmentEditor;
    private EnvironmentVariableRow? _activeEnvironmentRow;
    private bool _suppressJsonInputChanged;
    private bool _environmentVariablesLoaded;
    private bool _hostsLoaded;
    private string _loadedHostsContent = string.Empty;

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _copySuccessPopupText = new TextBlock
        {
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var copySuccessContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        _copySuccessPopupIcon = new FontIcon
        {
            Glyph = "\uE73E",
            FontSize = 12,
        };
        copySuccessContent.Children.Add(_copySuccessPopupIcon);
        copySuccessContent.Children.Add(_copySuccessPopupText);
        _copySuccessPopupSurface = new Border
        {
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Child = copySuccessContent,
        };
        _copySuccessPopup = new Popup
        {
            IsLightDismissEnabled = false,
            Child = _copySuccessPopupSurface,
        };
        ViewModel.NotificationRequested += OnNotificationRequested;
        ViewModel.ModulePreferencesChanged += OnModulePreferencesChanged;
        ViewModel.JsonFormatterHistory.CollectionChanged += (_, _) => UpdateJsonHistoryEmptyState();
        ViewModel.EnvironmentVariables.CollectionChanged += (_, _) => UpdateEnvironmentVariablesEmptyState();
        _jsonPreviewTimer.Tick += OnJsonPreviewTimerTick;
        UpdateJsonHistoryEmptyState();
        UpdateEnvironmentVariablesEmptyState();
        AppWindow.Resize(new SizeInt32(1280, 800));
        WindowPositioning.CenterOnPrimaryDisplay(AppWindow);
        RootNavigation.SelectedItem = NodeNavigationItem;
        SetRuntimeSection(showInstalled: true);
        _ = InitializeAsync();
    }

    public MainViewModel ViewModel { get; }

    private async Task InitializeAsync()
    {
        await ViewModel.InitializeAsync();
        ApplyRuntimeNavigationOrder();
        RootNavigation.SelectedItem = GetInitialNavigationItem();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var isRuntime = tag.StartsWith("runtime:", StringComparison.Ordinal);
        _currentTag = tag;
        RuntimesView.Visibility = isRuntime ? Visibility.Visible : Visibility.Collapsed;
        GitBashView.Visibility = tag == "git" ? Visibility.Visible : Visibility.Collapsed;
        ToolboxView.Visibility = tag == "toolbox" ? Visibility.Visible : Visibility.Collapsed;
        TasksView.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;
        PageRefreshButton.Visibility = tag == "toolbox" ? Visibility.Collapsed : Visibility.Visible;

        if (isRuntime && TryGetRuntimeKind(tag, out var kind))
        {
            ViewModel.SelectRuntimeModule(kind);
        }

        UpdatePageTitle();
    }

    private async void OnInstallVersionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RuntimeVersionRow row)
        {
            await ViewModel.InstallVersionAsync(row);
        }
    }

    private void OnInstalledTabClick(object sender, RoutedEventArgs e) =>
        SetRuntimeSection(showInstalled: true);

    private void OnVersionManagementTabClick(object sender, RoutedEventArgs e) =>
        SetRuntimeSection(showInstalled: false);

    private void SetRuntimeSection(bool showInstalled)
    {
        InstalledTabContent.Visibility = showInstalled ? Visibility.Visible : Visibility.Collapsed;
        VersionManagementTabContent.Visibility = showInstalled ? Visibility.Collapsed : Visibility.Visible;
        InstalledTabButton.IsChecked = showInstalled;
        VersionManagementTabButton.IsChecked = !showInstalled;
        _ = VisualStateManager.GoToState(
            InstalledTabButton,
            showInstalled ? "Checked" : "Unchecked",
            useTransitions: true);
        _ = VisualStateManager.GoToState(
            VersionManagementTabButton,
            showInstalled ? "Unchecked" : "Checked",
            useTransitions: true);
    }

    private async void OnInstalledEnvironmentClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button
            && button.DataContext is InstalledRuntimeRow row
            && row.CanToggleEnvironment)
        {
            button.IsEnabled = false;
            if (row.IsCurrent)
            {
                await ViewModel.ClearInstalledGlobalAsync(row);
            }
            else
            {
                await ViewModel.UseInstalledRuntimeAsync(row);
            }
            button.IsEnabled = true;
        }
    }

    private async void OnUninstallVersionClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is RuntimeVersionRow row)
        {
            var confirmation = await ConfirmUninstallAsync(row.RuntimeKind, row.Version);
            if (confirmation.Confirmed)
            {
                await ViewModel.UninstallVersionAsync(row, confirmation.DeleteData);
            }
        }
    }

    private async void OnUninstallInstalledClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledRuntimeRow row)
        {
            var confirmation = await ConfirmUninstallAsync(row.RuntimeKind, row.Version);
            if (confirmation.Confirmed)
            {
                await ViewModel.UninstallInstalledRuntimeAsync(row, confirmation.DeleteData);
            }
        }
    }

    private async void OnStartInstalledServiceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledRuntimeRow row)
        {
            await ViewModel.StartInstalledServiceAsync(row);
        }
    }

    private async void OnStopInstalledServiceClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledRuntimeRow row)
        {
            await ViewModel.StopInstalledServiceAsync(row);
        }
    }

    private async void OnOpenInstalledPathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledRuntimeRow row)
        {
            return;
        }

        try
        {
            var directory = Directory.Exists(row.Path) ? row.Path : Path.GetDirectoryName(row.Path);
            if (directory is null || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException(row.Path);
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(directory);
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                throw new InvalidOperationException("Windows could not open the folder.");
            }
        }
        catch (Exception exception)
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to open folder" : "无法打开目录",
                exception.Message,
                IsError: true));
        }
    }

    private void OnCopyInstalledPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: InstalledRuntimeRow row } target)
        {
            return;
        }

        CopyTextWithFeedback(
            target,
            row.Path,
            ViewModel.IsEnglish ? "Copied" : "已复制",
            ViewModel.IsEnglish ? "Unable to copy path" : "无法复制路径");
    }

    private async void OnCopyMySqlPasswordClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: InstalledRuntimeRow row } button)
        {
            return;
        }

        button.IsEnabled = false;
        try
        {
            var credentials = await ViewModel.GetMySqlCredentialsAsync(row.Version);
            CopyTextWithFeedback(
                button,
                credentials.Password,
                ViewModel.IsEnglish ? "Password copied" : "密码已复制",
                ViewModel.IsEnglish ? "Unable to copy MySQL password" : "无法复制 MySQL 密码");
        }
        catch (Exception exception)
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to copy MySQL password" : "无法复制 MySQL 密码",
                exception.Message,
                IsError: true));
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    private async void OnSaveMySqlPortClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledRuntimeRow row)
        {
            await ViewModel.SaveMySqlPortAsync(row);
        }
    }

    private void OnCopyGitBashPathClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target)
        {
            return;
        }

        CopyTextWithFeedback(
            target,
            ViewModel.GitBashInstallPath,
            ViewModel.IsEnglish ? "Copied" : "已复制",
            ViewModel.IsEnglish ? "Unable to copy path" : "无法复制路径");
    }

    private async void OnOpenRuntimeUrlClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RuntimeVersionRow row, Tag: string url }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || !IsOfficialRuntimeUri(row.RuntimeKind, uri))
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to open link" : "无法打开链接",
                ViewModel.IsEnglish ? "The version URL is invalid or is not an official source." : "版本地址无效或不是官方来源。",
                IsError: true));
            return;
        }

        if (!await Launcher.LaunchUriAsync(uri))
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to open link" : "无法打开链接",
                ViewModel.IsEnglish ? "Windows could not open the version URL." : "Windows 未能打开该版本地址。",
                IsError: true));
        }
    }

    private async void OnInstallOrUpgradeGitBashClick(object sender, RoutedEventArgs e) =>
        await ViewModel.InstallOrUpgradeGitBashAsync();

    private async void OnUninstallGitBashClick(object sender, RoutedEventArgs e)
    {
        if (RootNavigation.XamlRoot is null)
        {
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Uninstall Git?" : "确认卸载 Git？",
                Content = ViewModel.IsEnglish
                    ? "The portable Git copy managed by SoftPilot will be permanently removed."
                    : "将永久删除 SoftPilot 管理的 Git 便携副本。",
                PrimaryButtonText = ViewModel.IsEnglish ? "Uninstall" : "卸载",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.UninstallGitBashAsync();
            }
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private void OnLaunchGitBashClick(object sender, RoutedEventArgs e) =>
        LaunchGitBash(runAsAdministrator: false);

    private async void OnOpenGitBashFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Directory.Exists(ViewModel.GitBashInstallPath))
            {
                throw new DirectoryNotFoundException(ViewModel.GitBashInstallPath);
            }

            var folder = await StorageFolder.GetFolderFromPathAsync(ViewModel.GitBashInstallPath);
            if (!await Launcher.LaunchFolderAsync(folder))
            {
                throw new InvalidOperationException("Windows could not open the folder.");
            }
        }
        catch (Exception exception)
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to open folder" : "无法打开目录",
                exception.Message,
                IsError: true));
        }
    }

    private void OnLaunchGitBashAsAdministratorClick(object sender, RoutedEventArgs e) =>
        LaunchGitBash(runAsAdministrator: true);

    private void LaunchGitBash(bool runAsAdministrator)
    {
        try
        {
            if (!File.Exists(ViewModel.GitBashLauncherPath))
            {
                throw new FileNotFoundException("git-bash.exe was not found.", ViewModel.GitBashLauncherPath);
            }

            Process.Start(new ProcessStartInfo(ViewModel.GitBashLauncherPath)
            {
                UseShellExecute = true,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Verb = runAsAdministrator ? "runas" : string.Empty,
            });
        }
        catch (Exception exception)
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to launch Git Bash" : "无法启动 Git Bash",
                exception.Message,
                IsError: true));
        }
    }

    private async void OnOpenGitBashReleaseClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string address }
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri)
            || !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
            || !uri.AbsolutePath.StartsWith("/git-for-windows/git/releases/tag/", StringComparison.Ordinal))
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to open link" : "无法打开链接",
                ViewModel.IsEnglish ? "The Git for Windows release URL is unavailable or invalid." : "Git for Windows 发布地址不可用或无效。",
                IsError: true));
            return;
        }

        if (!await Launcher.LaunchUriAsync(uri))
        {
            OnNotificationRequested(new UserNotification(
                ViewModel.IsEnglish ? "Unable to open link" : "无法打开链接",
                ViewModel.IsEnglish ? "Windows could not open the Git for Windows release page." : "Windows 未能打开 Git for Windows 发布页。",
                IsError: true));
        }
    }

    private async void OnSaveGitConfigurationClick(object sender, RoutedEventArgs e) =>
        await ViewModel.SaveGitConfigurationAsync();

    private async void OnJsonBeautifyClick(object sender, RoutedEventArgs e)
    {
        SetJsonFormattingMode(JsonFormattingMode.Beautified);
        await TransformJsonAsync(saveHistory: true, showEmptyWarning: true);
    }

    private void OnJsonToolClick(object sender, RoutedEventArgs e) =>
        ShowToolboxPanel(JsonFormatterPanel, JsonToolButton);

    private async void OnEnvironmentToolClick(object sender, RoutedEventArgs e)
    {
        ShowToolboxPanel(EnvironmentVariablesPanel, EnvironmentToolButton);
        if (!_environmentVariablesLoaded)
        {
            await RefreshEnvironmentVariablesAsync();
        }
    }

    private async void OnHostsToolClick(object sender, RoutedEventArgs e)
    {
        ShowToolboxPanel(HostsPanel, HostsToolButton);
        if (!_hostsLoaded)
        {
            await LoadHostsAsync(confirmDiscard: false);
        }
    }

    private void ShowToolboxPanel(FrameworkElement panel, ToggleButton activeButton)
    {
        JsonFormatterPanel.Visibility = panel == JsonFormatterPanel ? Visibility.Visible : Visibility.Collapsed;
        EnvironmentVariablesPanel.Visibility = panel == EnvironmentVariablesPanel ? Visibility.Visible : Visibility.Collapsed;
        HostsPanel.Visibility = panel == HostsPanel ? Visibility.Visible : Visibility.Collapsed;
        JsonHistoryPanel.Visibility = panel == JsonFormatterPanel ? Visibility.Visible : Visibility.Collapsed;
        JsonToolButton.IsChecked = activeButton == JsonToolButton;
        EnvironmentToolButton.IsChecked = activeButton == EnvironmentToolButton;
        HostsToolButton.IsChecked = activeButton == HostsToolButton;
    }

    private async void OnUserEnvironmentScopeClick(object sender, RoutedEventArgs e)
    {
        _environmentVariableScope = EnvironmentVariableScope.User;
        UserEnvironmentButton.IsChecked = true;
        MachineEnvironmentButton.IsChecked = false;
        await RefreshEnvironmentVariablesAsync();
    }

    private async void OnMachineEnvironmentScopeClick(object sender, RoutedEventArgs e)
    {
        _environmentVariableScope = EnvironmentVariableScope.Machine;
        UserEnvironmentButton.IsChecked = false;
        MachineEnvironmentButton.IsChecked = true;
        await RefreshEnvironmentVariablesAsync();
    }

    private async void OnRefreshEnvironmentVariablesClick(object sender, RoutedEventArgs e) =>
        await RefreshEnvironmentVariablesAsync();

    private async Task RefreshEnvironmentVariablesAsync()
    {
        CancelEnvironmentVariableInlineEdit();
        try
        {
            await ViewModel.RefreshEnvironmentVariablesAsync(_environmentVariableScope);
            RebuildEnvironmentVariableList();
            _environmentVariablesLoaded = true;
        }
        catch (Exception exception)
        {
            ShowToolboxError(
                ViewModel.IsEnglish ? "Unable to load environment variables" : "无法加载环境变量",
                exception);
        }
    }

    private async void OnAddEnvironmentVariableClick(object sender, RoutedEventArgs e) =>
        await ShowNewEnvironmentVariableDialogAsync();

    private void OnOpenSystemEnvironmentVariablesClick(object sender, RoutedEventArgs e) =>
        OpenSystemEnvironmentVariables();

    private void OpenSystemEnvironmentVariables()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "rundll32.exe",
                Arguments = "sysdm.cpl,EditEnvironmentVariables",
                UseShellExecute = true,
            });
        }
        catch (Exception exception)
        {
            ShowToolboxError(
                ViewModel.IsEnglish
                    ? "Unable to open Windows environment variables"
                    : "无法打开 Windows 环境变量设置",
                exception);
        }
    }

    private async void OnEditEnvironmentVariableClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: EnvironmentVariableRow row } valueButton)
        {
            if (row.IsPath)
            {
                CancelEnvironmentVariableInlineEdit();
                await ShowPathEnvironmentVariableEditorAsync(row);
            }
            else
            {
                BeginEnvironmentVariableInlineEdit(valueButton, row);
            }
        }
    }

    private void BeginEnvironmentVariableInlineEdit(Button valueButton, EnvironmentVariableRow row)
    {
        CancelEnvironmentVariableInlineEdit();
        if (valueButton.Parent is not Grid rowGrid)
        {
            return;
        }

        var saveButton = rowGrid.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Tag?.ToString(), "EnvironmentSave", StringComparison.Ordinal));
        var cancelButton = rowGrid.Children
            .OfType<Button>()
            .FirstOrDefault(button => string.Equals(button.Tag?.ToString(), "EnvironmentCancel", StringComparison.Ordinal));
        if (saveButton is null || cancelButton is null)
        {
            return;
        }

        var editor = new TextBox
        {
            Text = row.Value,
            MinHeight = 32,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        Grid.SetColumn(editor, 1);
        editor.KeyDown += async (_, args) =>
        {
            if (args.Key == VirtualKey.Enter)
            {
                args.Handled = true;
                await SaveEnvironmentVariableInlineAsync();
            }
            else if (args.Key == VirtualKey.Escape)
            {
                args.Handled = true;
                CancelEnvironmentVariableInlineEdit();
            }
        };

        valueButton.Visibility = Visibility.Collapsed;
        saveButton.Visibility = Visibility.Visible;
        cancelButton.Visibility = Visibility.Visible;
        rowGrid.Children.Add(editor);

        _activeEnvironmentEditRow = rowGrid;
        _activeEnvironmentValueButton = valueButton;
        _activeEnvironmentSaveButton = saveButton;
        _activeEnvironmentCancelButton = cancelButton;
        _activeEnvironmentEditor = editor;
        _activeEnvironmentRow = row;
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
    }

    private async void OnSaveEnvironmentVariableInlineClick(object sender, RoutedEventArgs e) =>
        await SaveEnvironmentVariableInlineAsync();

    private void OnCancelEnvironmentVariableInlineClick(object sender, RoutedEventArgs e) =>
        CancelEnvironmentVariableInlineEdit();

    private async Task SaveEnvironmentVariableInlineAsync()
    {
        if (_activeEnvironmentEditor is not { } editor
            || _activeEnvironmentRow is not { } row
            || _activeEnvironmentSaveButton is not { } saveButton)
        {
            return;
        }

        saveButton.IsEnabled = false;
        try
        {
            await SaveEnvironmentVariableWithElevationAsync(row.Name, editor.Text, row.Scope);
            RebuildEnvironmentVariableList();
            ClearEnvironmentVariableInlineEditState();
            ShowToolboxSuccess(
                ViewModel.IsEnglish ? "Environment variable saved" : "环境变量已保存",
                row.Name);
        }
        catch (Exception exception)
        {
            saveButton.IsEnabled = true;
            ShowToolboxError(
                ViewModel.IsEnglish ? "Unable to save environment variable" : "无法保存环境变量",
                exception);
        }
    }

    private void CancelEnvironmentVariableInlineEdit()
    {
        if (_activeEnvironmentEditRow is { } rowGrid && _activeEnvironmentEditor is { } editor)
        {
            rowGrid.Children.Remove(editor);
        }

        if (_activeEnvironmentValueButton is { } valueButton)
        {
            valueButton.Visibility = Visibility.Visible;
        }

        if (_activeEnvironmentSaveButton is { } saveButton)
        {
            saveButton.Visibility = Visibility.Collapsed;
            saveButton.IsEnabled = true;
        }

        if (_activeEnvironmentCancelButton is { } cancelButton)
        {
            cancelButton.Visibility = Visibility.Collapsed;
        }

        ClearEnvironmentVariableInlineEditState();
    }

    private void ClearEnvironmentVariableInlineEditState()
    {
        _activeEnvironmentEditRow = null;
        _activeEnvironmentValueButton = null;
        _activeEnvironmentSaveButton = null;
        _activeEnvironmentCancelButton = null;
        _activeEnvironmentEditor = null;
        _activeEnvironmentRow = null;
    }

    private async Task ShowPathEnvironmentVariableEditorAsync(EnvironmentVariableRow row)
    {
        if (RootNavigation.XamlRoot is null)
        {
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var entries = new ObservableCollection<PathEnvironmentEntry>(
                EnvironmentPathValue.Split(row.Value)
                    .Select(value => new PathEnvironmentEntry(value, ViewModel.IsEnglish)));
            var entryList = new ItemsControl
            {
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            var entryScrollViewer = new ScrollViewer
            {
                Content = entryList,
                MinHeight = 360,
                MaxHeight = 460,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            var validationText = new TextBlock
            {
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
                TextWrapping = TextWrapping.Wrap,
            };
            PathEnvironmentEntry? activeEntry = null;
            TextBox? activeEntryEditor = null;
            string? activeEntryOriginalValue = null;

            Button CreateIconButton(string glyph, string toolTip, bool isDestructive = false)
            {
                var icon = new FontIcon { Glyph = glyph, FontSize = 13 };
                if (isDestructive)
                {
                    icon.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Firebrick);
                }

                var button = new StableCursorButton
                {
                    Width = 30,
                    Height = 30,
                    Style = (Style)RootNavigation.Resources["IconButtonStyle"],
                    Content = icon,
                };
                ToolTipService.SetToolTip(button, toolTip);
                return button;
            }

            void ShowNewEntryFlyout(Button anchor)
            {
                var valueBox = new TextBox
                {
                    Header = ViewModel.IsEnglish ? "Path entry" : "路径项",
                    PlaceholderText = ViewModel.IsEnglish
                        ? @"For example: C:\Tools or %JAVA_HOME%\bin"
                        : @"例如：C:\Tools 或 %JAVA_HOME%\bin",
                    MinWidth = 420,
                };
                var errorText = new TextBlock
                {
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Firebrick),
                    TextWrapping = TextWrapping.Wrap,
                };
                var applyButton = new StableCursorButton
                {
                    Content = ViewModel.IsEnglish ? "Add" : "新增",
                    Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"],
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                var flyoutContent = new StackPanel { Spacing = 10 };
                flyoutContent.Children.Add(valueBox);
                flyoutContent.Children.Add(errorText);
                flyoutContent.Children.Add(applyButton);
                var flyout = new Flyout
                {
                    Content = flyoutContent,
                    Placement = FlyoutPlacementMode.Bottom,
                };
                applyButton.Click += (_, _) =>
                {
                    try
                    {
                        EnvironmentPathValue.ValidateEntry(valueBox.Text);
                        var item = new PathEnvironmentEntry(valueBox.Text, ViewModel.IsEnglish);
                        entries.Add(item);

                        validationText.Text = string.Empty;
                        RefreshRows();
                        flyout.Hide();
                    }
                    catch (Exception exception)
                    {
                        errorText.Text = exception.Message;
                    }
                };
                flyout.Opened += (_, _) => valueBox.Focus(FocusState.Programmatic);
                flyout.ShowAt(anchor);
            }

            bool EndActiveEntryEdit(bool commit)
            {
                if (activeEntry is null)
                {
                    return true;
                }

                if (!commit)
                {
                    activeEntry.Value = activeEntryOriginalValue ?? activeEntry.Value;
                }

                try
                {
                    EnvironmentPathValue.ValidateEntry(activeEntry.Value);
                    validationText.Text = string.Empty;
                }
                catch (Exception exception)
                {
                    validationText.Text = exception.Message;
                    activeEntryEditor?.Focus(FocusState.Programmatic);
                    return false;
                }

                activeEntry = null;
                activeEntryEditor = null;
                activeEntryOriginalValue = null;
                RefreshRows();
                return true;
            }

            void BeginEntryEdit(PathEnvironmentEntry item)
            {
                if (ReferenceEquals(activeEntry, item))
                {
                    activeEntryEditor?.Focus(FocusState.Programmatic);
                    return;
                }

                if (!EndActiveEntryEdit(commit: true))
                {
                    return;
                }

                var index = entries.IndexOf(item);
                if (index < 0 || entryList.Items[index] is not Grid rowGrid)
                {
                    return;
                }

                var valueButton = rowGrid.Children
                    .OfType<Button>()
                    .FirstOrDefault(button => string.Equals(button.Tag?.ToString(), "PathValue", StringComparison.Ordinal));
                if (valueButton is null)
                {
                    return;
                }

                var originalValue = item.Value;
                var editor = new TextBox
                {
                    Text = item.Value,
                    MinWidth = 0,
                    MinHeight = 32,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                Grid.SetColumn(editor, 0);
                editor.TextChanged += (_, _) => item.Value = editor.Text;
                editor.KeyDown += (_, args) =>
                {
                    if (args.Key == VirtualKey.Enter)
                    {
                        args.Handled = true;
                        _ = EndActiveEntryEdit(commit: true);
                    }
                    else if (args.Key == VirtualKey.Escape)
                    {
                        args.Handled = true;
                        _ = EndActiveEntryEdit(commit: false);
                    }
                };

                valueButton.Visibility = Visibility.Collapsed;
                rowGrid.Children.Add(editor);
                activeEntry = item;
                activeEntryEditor = editor;
                activeEntryOriginalValue = originalValue;
                editor.Focus(FocusState.Programmatic);
                editor.SelectAll();
            }

            void MoveEntry(PathEnvironmentEntry item, int offset)
            {
                if (!EndActiveEntryEdit(commit: true))
                {
                    return;
                }

                var currentIndex = entries.IndexOf(item);
                var targetIndex = currentIndex + offset;
                if (currentIndex < 0 || targetIndex < 0 || targetIndex >= entries.Count)
                {
                    return;
                }

                entries.Move(currentIndex, targetIndex);
                RefreshRows();
            }

            Grid CreateRow(int index)
            {
                var item = entries[index];
                var resolution = EnvironmentPathValue.Resolve(item.Value);
                var rowGrid = new Grid
                {
                    MinHeight = 46,
                    Padding = new Thickness(12, 6, 8, 6),
                    ColumnSpacing = 12,
                };
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });

                var valueText = new TextBlock
                {
                    Text = string.IsNullOrEmpty(item.Value)
                        ? ViewModel.IsEnglish ? "(empty entry)" : "（空条目）"
                        : item.Value,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                var valueButton = new StableCursorButton
                {
                    Style = (Style)RootNavigation.Resources["CellValueButtonStyle"],
                    Content = valueText,
                    Tag = "PathValue",
                };
                if (!resolution.Exists)
                {
                    valueText.Foreground = new SolidColorBrush(Microsoft.UI.Colors.Firebrick);
                }

                ToolTipService.SetToolTip(
                    valueButton,
                    string.Equals(item.Value, resolution.ExpandedValue, StringComparison.Ordinal)
                        ? resolution.Exists
                            ? item.Value
                            : ViewModel.IsEnglish
                                ? $"{item.Value}\nThis path does not exist."
                                : $"{item.Value}\n此路径不存在。"
                        : resolution.Exists
                            ? ViewModel.IsEnglish
                                ? $"Resolved path: {resolution.ExpandedValue}"
                                : $"实际路径：{resolution.ExpandedValue}"
                            : ViewModel.IsEnglish
                                ? $"Resolved path: {resolution.ExpandedValue}\nThis path does not exist."
                                : $"实际路径：{resolution.ExpandedValue}\n此路径不存在。");
                valueButton.Click += (_, _) => BeginEntryEdit(item);

                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    HorizontalAlignment = HorizontalAlignment.Right,
                };
                var moveDown = CreateIconButton(
                    "\uE74B",
                    ViewModel.IsEnglish ? "Move down" : "下移");
                moveDown.IsEnabled = index < entries.Count - 1;
                moveDown.Click += (_, _) => MoveEntry(item, 1);
                var moveUp = CreateIconButton(
                    "\uE74A",
                    ViewModel.IsEnglish ? "Move up" : "上移");
                moveUp.IsEnabled = index > 0;
                moveUp.Click += (_, _) => MoveEntry(item, -1);
                var delete = CreateIconButton(
                    "\uE74D",
                    ViewModel.IsEnglish ? "Delete" : "删除",
                    isDestructive: true);
                delete.Click += (_, _) =>
                {
                    if (!EndActiveEntryEdit(commit: true))
                    {
                        return;
                    }

                    entries.Remove(item);
                    RefreshRows();
                };
                actions.Children.Add(moveDown);
                actions.Children.Add(moveUp);
                actions.Children.Add(delete);

                Grid.SetColumn(actions, 1);
                rowGrid.Children.Add(valueButton);
                rowGrid.Children.Add(actions);
                return rowGrid;
            }

            void RefreshRows()
            {
                entryList.Items.Clear();
                for (var index = 0; index < entries.Count; index++)
                {
                    entryList.Items.Add(CreateRow(index));
                }
            }

            var addButton = CreateIconButton(
                "\uE710",
                ViewModel.IsEnglish ? "Add PATH entry" : "新增 PATH 路径项");
            addButton.Click += (_, _) =>
            {
                if (EndActiveEntryEdit(commit: true))
                {
                    ShowNewEntryFlyout(addButton);
                }
            };
            var refreshButton = CreateIconButton(
                "\uE72C",
                ViewModel.IsEnglish ? "Refresh resolved paths" : "刷新实际路径");
            refreshButton.Click += (_, _) =>
            {
                if (EndActiveEntryEdit(commit: true))
                {
                    RefreshRows();
                }
            };
            var toolbar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            toolbar.Children.Add(addButton);
            toolbar.Children.Add(refreshButton);

            var header = new Grid
            {
                Padding = new Thickness(12, 8, 8, 8),
                ColumnSpacing = 12,
            };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(108) });
            var valueHeader = new TextBlock
            {
                Text = ViewModel.IsEnglish ? "Value" : "值",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            };
            var actionHeader = new TextBlock
            {
                Text = ViewModel.IsEnglish ? "Actions" : "操作",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            Grid.SetColumn(actionHeader, 1);
            header.Children.Add(valueHeader);
            header.Children.Add(actionHeader);

            var table = new Grid();
            table.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            table.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(entryScrollViewer, 1);
            table.Children.Add(header);
            table.Children.Add(entryScrollViewer);

            var content = new Grid { RowSpacing = 8, Width = 860 };
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(table, 1);
            Grid.SetRow(validationText, 2);
            content.Children.Add(toolbar);
            content.Children.Add(table);
            content.Children.Add(validationText);
            RefreshRows();

            string? valueToSave = null;
            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Edit Path environment variable" : "编辑 Path 环境变量",
                Content = content,
                PrimaryButtonText = ViewModel.IsEnglish ? "Save" : "保存",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Primary,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            dialog.Resources["ContentDialogMaxWidth"] = 1060d;
            dialog.PrimaryButtonClick += (_, args) =>
            {
                try
                {
                    if (!EndActiveEntryEdit(commit: true))
                    {
                        args.Cancel = true;
                        return;
                    }

                    valueToSave = EnvironmentPathValue.Join(entries.Select(entry => entry.Value));
                    validationText.Text = string.Empty;
                }
                catch (Exception exception)
                {
                    validationText.Text = exception.Message;
                    args.Cancel = true;
                }
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary || valueToSave is null)
            {
                return;
            }

            await SaveEnvironmentVariableWithElevationAsync(row.Name, valueToSave, row.Scope);
            RebuildEnvironmentVariableList();
            ShowToolboxSuccess(
                ViewModel.IsEnglish ? "Path saved" : "Path 已保存",
                ViewModel.IsEnglish
                    ? $"Saved {entries.Count} PATH entries."
                    : $"已保存 {entries.Count} 个 PATH 路径项。");
        }
        catch (Exception exception)
        {
            ShowToolboxError(ViewModel.IsEnglish ? "Unable to save Path" : "无法保存 Path", exception);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task ShowNewEnvironmentVariableDialogAsync()
    {
        if (RootNavigation.XamlRoot is null)
        {
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var nameBox = new TextBox
            {
                Header = ViewModel.EnvironmentNameHeaderText,
                MaxLength = 255,
            };
            var valueBox = new TextBox
            {
                Header = ViewModel.EnvironmentValueHeaderText,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120,
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(nameBox);
            content.Children.Add(valueBox);

            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Add environment variable" : "新增环境变量",
                Content = content,
                PrimaryButtonText = ViewModel.IsEnglish ? "Save" : "保存",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await SaveEnvironmentVariableWithElevationAsync(
                nameBox.Text,
                valueBox.Text,
                _environmentVariableScope);
            RebuildEnvironmentVariableList();
            ShowToolboxSuccess(
                ViewModel.IsEnglish ? "Environment variable saved" : "环境变量已保存",
                nameBox.Text.Trim());
        }
        catch (Exception exception)
        {
            ShowToolboxError(
                ViewModel.IsEnglish ? "Unable to save environment variable" : "无法保存环境变量",
                exception);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async void OnDeleteEnvironmentVariableClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: EnvironmentVariableRow row }
            || RootNavigation.XamlRoot is null)
        {
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Delete environment variable?" : "删除环境变量？",
                Content = row.Name,
                PrimaryButtonText = ViewModel.IsEnglish ? "Delete" : "删除",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            await DeleteEnvironmentVariableWithElevationAsync(row.Name, row.Scope);
            RebuildEnvironmentVariableList();
            ShowToolboxSuccess(
                ViewModel.IsEnglish ? "Environment variable deleted" : "环境变量已删除",
                row.Name);
        }
        catch (Exception exception)
        {
            ShowToolboxError(
                ViewModel.IsEnglish ? "Unable to delete environment variable" : "无法删除环境变量",
                exception);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async void OnReloadHostsClick(object sender, RoutedEventArgs e) =>
        await LoadHostsAsync(confirmDiscard: true);

    private async Task LoadHostsAsync(bool confirmDiscard)
    {
        if (confirmDiscard
            && HasUnsavedHostsChanges()
            && !await ConfirmDiscardHostsChangesAsync())
        {
            return;
        }

        try
        {
            HostsEditorTextBox.Text = await ViewModel.ReadHostsAsync();
            _loadedHostsContent = HostsEditorTextBox.Text;
            _hostsLoaded = true;
        }
        catch (Exception exception)
        {
            ShowToolboxError(ViewModel.IsEnglish ? "Unable to load Hosts" : "无法加载 Hosts", exception);
        }
    }

    private async Task<bool> ConfirmDiscardHostsChangesAsync()
    {
        if (RootNavigation.XamlRoot is null)
        {
            return false;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Discard unsaved Hosts changes?" : "放弃未保存的 Hosts 修改？",
                PrimaryButtonText = ViewModel.IsEnglish ? "Discard" : "放弃",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async void OnSaveHostsClick(object sender, RoutedEventArgs e)
        => await SaveHostsAsync(sender as Button);

    private async void OnHostsEditorKeyDown(object sender, KeyRoutedEventArgs args)
    {
        var controlState = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        if (args.Key != VirtualKey.S || !controlState.HasFlag(CoreVirtualKeyStates.Down))
        {
            return;
        }

        args.Handled = true;
        await SaveHostsAsync(HostsSaveButton);
    }

    private async Task SaveHostsAsync(Button? saveButton)
    {
        if (saveButton is not null)
        {
            saveButton.IsEnabled = false;
        }

        try
        {
            var content = HostsEditorTextBox.Text;
            try
            {
                await ViewModel.SaveHostsAsync(content);
            }
            catch (AdministratorPrivilegesRequiredException)
            {
                await ElevatedOperationBroker.SaveHostsAsync(content);
            }

            _loadedHostsContent = HostsEditorTextBox.Text;
            _hostsLoaded = true;
            ShowToolboxSuccess(
                ViewModel.IsEnglish ? "Hosts saved" : "Hosts 已保存",
                ViewModel.HostsPath);
        }
        catch (Exception exception)
        {
            ShowToolboxError(ViewModel.IsEnglish ? "Unable to save Hosts" : "无法保存 Hosts", exception);
        }
        finally
        {
            if (saveButton is not null)
            {
                saveButton.IsEnabled = true;
            }
        }
    }

    private async Task SaveEnvironmentVariableWithElevationAsync(
        string name,
        string value,
        EnvironmentVariableScope scope)
    {
        try
        {
            await ViewModel.SaveEnvironmentVariableAsync(name, value, scope);
        }
        catch (AdministratorPrivilegesRequiredException) when (scope == EnvironmentVariableScope.Machine)
        {
            await ElevatedOperationBroker.SetEnvironmentVariableAsync(name, value);
            await ViewModel.RefreshEnvironmentVariablesAsync(scope);
        }
    }

    private async Task DeleteEnvironmentVariableWithElevationAsync(
        string name,
        EnvironmentVariableScope scope)
    {
        try
        {
            await ViewModel.DeleteEnvironmentVariableAsync(name, scope);
        }
        catch (AdministratorPrivilegesRequiredException) when (scope == EnvironmentVariableScope.Machine)
        {
            await ElevatedOperationBroker.DeleteEnvironmentVariableAsync(name);
            await ViewModel.RefreshEnvironmentVariablesAsync(scope);
        }
    }

    private bool HasUnsavedHostsChanges() =>
        _hostsLoaded
        && !string.Equals(HostsEditorTextBox.Text, _loadedHostsContent, StringComparison.Ordinal);

    private void RebuildEnvironmentVariableList()
    {
        EnvironmentVariablesList.ItemsSource = null;
        EnvironmentVariablesList.ItemsSource = ViewModel.EnvironmentVariables;
        UpdateEnvironmentVariablesEmptyState();
    }

    private void UpdateEnvironmentVariablesEmptyState() =>
        EnvironmentVariablesEmptyText.Visibility = ViewModel.EnvironmentVariables.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void ShowToolboxSuccess(string title, string message) =>
        OnNotificationRequested(new UserNotification(title, message, IsError: false, AutoDismiss: true));

    private void ShowToolboxError(string title, Exception exception) =>
        OnNotificationRequested(new UserNotification(title, exception.Message, IsError: true));

    private async void OnJsonMinifyClick(object sender, RoutedEventArgs e)
    {
        SetJsonFormattingMode(JsonFormattingMode.Minified);
        await TransformJsonAsync(saveHistory: true, showEmptyWarning: true);
    }

    private async void OnJsonPreviewTimerTick(object? sender, object e)
    {
        _jsonPreviewTimer.Stop();
        await TransformJsonAsync(saveHistory: true, showEmptyWarning: false);
    }

    private async Task TransformJsonAsync(bool saveHistory, bool showEmptyWarning)
    {
        await _jsonTransformGate.WaitAsync();
        try
        {
            var input = JsonInputTextBox.Text;
            if (string.IsNullOrWhiteSpace(input))
            {
                JsonCopyButton.IsEnabled = false;
                JsonOutputTextBox.Text = showEmptyWarning
                    ? ViewModel.IsEnglish
                        ? "JSON is empty\r\nPaste or type JSON before formatting."
                        : "JSON 为空\r\n请先粘贴或输入 JSON。"
                    : string.Empty;

                return;
            }

            try
            {
                JsonOutputTextBox.Text = _jsonFormattingMode == JsonFormattingMode.Beautified
                    ? JsonTextFormatter.Beautify(input)
                    : JsonTextFormatter.Minify(input);
                JsonCopyButton.IsEnabled = true;

                if (saveHistory)
                {
                    try
                    {
                        _currentJsonHistoryId = await ViewModel.UpsertJsonFormatterHistoryAsync(
                            _currentJsonHistoryId,
                            input,
                            _jsonFormattingMode);
                    }
                    catch (Exception exception)
                    {
                        NotifyJsonHistoryError(
                            ViewModel.IsEnglish ? "Unable to save JSON history" : "无法保存 JSON 历史记录",
                            exception);
                    }
                }
            }
            catch (JsonException exception)
            {
                JsonOutputTextBox.Text = string.Empty;
                JsonCopyButton.IsEnabled = false;
                var position = exception.LineNumber is not null && exception.BytePositionInLine is not null
                    ? ViewModel.IsEnglish
                        ? $"Line {exception.LineNumber + 1}, column {exception.BytePositionInLine + 1}."
                        : $"第 {exception.LineNumber + 1} 行，第 {exception.BytePositionInLine + 1} 列。"
                    : string.Empty;
                var title = ViewModel.IsEnglish ? "Invalid JSON" : "JSON 格式错误";
                var detail = string.IsNullOrEmpty(position)
                    ? exception.Message
                    : $"{position} {exception.Message}";
                JsonOutputTextBox.Text = $"{title}{Environment.NewLine}{detail}";
            }
        }
        finally
        {
            _jsonTransformGate.Release();
        }
    }

    private void SetJsonFormattingMode(JsonFormattingMode mode)
    {
        _jsonFormattingMode = mode;
        JsonBeautifyButton.IsChecked = mode == JsonFormattingMode.Beautified;
        JsonMinifyButton.IsChecked = mode == JsonFormattingMode.Minified;
    }

    private void OnJsonCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target
            || !JsonCopyButton.IsEnabled
            || string.IsNullOrEmpty(JsonOutputTextBox.Text))
        {
            return;
        }

        CopyTextWithFeedback(
            target,
            JsonOutputTextBox.Text,
            ViewModel.IsEnglish ? "Copied" : "已复制",
            ViewModel.IsEnglish ? "Unable to copy result" : "无法复制结果");
    }

    private void CopyTextWithFeedback(
        FrameworkElement target,
        string text,
        string successMessage,
        string failureTitle)
    {
        try
        {
            var package = new DataPackage();
            package.SetText(text);
            Clipboard.SetContent(package);
        }
        catch (Exception exception)
        {
            OnNotificationRequested(new UserNotification(failureTitle, exception.Message, IsError: true));
            return;
        }

        ShowCopySuccess(target, successMessage);
    }

    private void ShowCopySuccess(FrameworkElement target, string message)
    {
        _copySuccessCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _copySuccessCancellation = cancellation;

        _copySuccessPopup.IsOpen = false;
        _copySuccessPopup.XamlRoot = target.XamlRoot;
        _copySuccessPopupText.Text = message;
        ApplyCopySuccessTheme();
        _copySuccessPopupSurface.Opacity = 0;
        _copySuccessPopupSurface.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));

        var targetPosition = target.TransformToVisual(null)
            .TransformPoint(new Windows.Foundation.Point(0, 0));
        var popupSize = _copySuccessPopupSurface.DesiredSize;
        var rootWidth = RootNavigation.ActualWidth;
        var horizontalOffset = targetPosition.X + ((target.ActualWidth - popupSize.Width) / 2);
        horizontalOffset = Math.Clamp(horizontalOffset, 8, Math.Max(8, rootWidth - popupSize.Width - 8));

        var verticalOffset = targetPosition.Y - popupSize.Height - 8;
        if (verticalOffset < 8)
        {
            verticalOffset = targetPosition.Y + target.ActualHeight + 8;
        }

        _copySuccessPopup.HorizontalOffset = horizontalOffset;
        _copySuccessPopup.VerticalOffset = verticalOffset;
        _copySuccessPopup.IsOpen = true;
        _ = AnimateCopySuccessAsync(cancellation);
    }

    private void ApplyCopySuccessTheme()
    {
        var isDark = RootNavigation.ActualTheme == ElementTheme.Dark;
        var background = isDark
            ? Windows.UI.Color.FromArgb(255, 28, 47, 31)
            : Windows.UI.Color.FromArgb(255, 240, 249, 235);
        var border = isDark
            ? Windows.UI.Color.FromArgb(255, 57, 89, 59)
            : Windows.UI.Color.FromArgb(255, 179, 225, 157);
        var foreground = isDark
            ? Windows.UI.Color.FromArgb(255, 135, 204, 122)
            : Windows.UI.Color.FromArgb(255, 62, 122, 34);

        _copySuccessPopupSurface.Background = new SolidColorBrush(background);
        _copySuccessPopupSurface.BorderBrush = new SolidColorBrush(border);
        _copySuccessPopupIcon.Foreground = new SolidColorBrush(foreground);
        _copySuccessPopupText.Foreground = new SolidColorBrush(foreground);
    }

    private async Task AnimateCopySuccessAsync(CancellationTokenSource cancellation)
    {
        try
        {
            for (var step = 1; step <= 4; step++)
            {
                _copySuccessPopupSurface.Opacity = step / 4d;
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellation.Token);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(950), cancellation.Token);
            for (var step = 1; step <= 6; step++)
            {
                _copySuccessPopupSurface.Opacity = 1 - (step / 6d);
                await Task.Delay(TimeSpan.FromMilliseconds(40), cancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // A newer copy action replaced this prompt.
        }
        finally
        {
            if (ReferenceEquals(_copySuccessCancellation, cancellation))
            {
                _copySuccessPopup.IsOpen = false;
                _copySuccessPopupSurface.Opacity = 1;
                _copySuccessCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private void OnJsonClearClick(object sender, RoutedEventArgs e)
    {
        ResetJsonEditor();
        JsonInputTextBox.Focus(FocusState.Programmatic);
    }

    private void OnJsonInputTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressJsonInputChanged)
        {
            return;
        }

        _jsonPreviewTimer.Stop();
        JsonCopyButton.IsEnabled = false;
        if (string.IsNullOrWhiteSpace(JsonInputTextBox.Text))
        {
            JsonOutputTextBox.Text = string.Empty;
            return;
        }

        _jsonPreviewTimer.Start();
    }

    private async void OnJsonHistorySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (JsonHistoryList.SelectedItem is not JsonFormatterHistoryRow row)
        {
            return;
        }

        _jsonPreviewTimer.Stop();
        _currentJsonHistoryId = row.Id;
        SetJsonFormattingMode(row.Mode);
        _suppressJsonInputChanged = true;
        JsonInputTextBox.Text = row.Input;
        _suppressJsonInputChanged = false;
        await TransformJsonAsync(saveHistory: false, showEmptyWarning: false);
    }

    private async void OnRenameJsonHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JsonFormatterHistoryRow row }
            || RootNavigation.XamlRoot is null)
        {
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var titleBox = new TextBox { Text = row.Title, MaxLength = 80 };
            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Rename JSON history" : "重命名 JSON 记录",
                Content = titleBox,
                PrimaryButtonText = ViewModel.IsEnglish ? "Save" : "保存",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.RenameJsonFormatterHistoryAsync(row.Id, titleBox.Text);
            }
        }
        catch (Exception exception)
        {
            NotifyJsonHistoryError(
                ViewModel.IsEnglish ? "Unable to rename JSON history" : "无法重命名 JSON 记录",
                exception);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async void OnDeleteJsonHistoryClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: JsonFormatterHistoryRow row })
        {
            return;
        }

        try
        {
            await ViewModel.DeleteJsonFormatterHistoryAsync(row.Id);
            if (_currentJsonHistoryId == row.Id)
            {
                ResetJsonEditor();
            }
        }
        catch (Exception exception)
        {
            NotifyJsonHistoryError(
                ViewModel.IsEnglish ? "Unable to delete JSON history" : "无法删除 JSON 记录",
                exception);
        }
    }

    private async void OnClearJsonHistoryClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.JsonFormatterHistory.Count == 0 || RootNavigation.XamlRoot is null)
        {
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Clear JSON history?" : "清空 JSON 历史记录？",
                Content = ViewModel.IsEnglish
                    ? "All saved JSON history will be permanently deleted."
                    : "所有已保存的 JSON 历史记录都将被永久删除。",
                PrimaryButtonText = ViewModel.IsEnglish ? "Clear" : "清空",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                await ViewModel.ClearJsonFormatterHistoryAsync();
                ResetJsonEditor();
            }
        }
        catch (Exception exception)
        {
            NotifyJsonHistoryError(
                ViewModel.IsEnglish ? "Unable to clear JSON history" : "无法清空 JSON 历史记录",
                exception);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private void ResetJsonEditor()
    {
        _jsonPreviewTimer.Stop();
        _currentJsonHistoryId = null;
        JsonHistoryList.SelectedItem = null;
        _suppressJsonInputChanged = true;
        JsonInputTextBox.Text = string.Empty;
        _suppressJsonInputChanged = false;
        JsonOutputTextBox.Text = string.Empty;
        JsonCopyButton.IsEnabled = false;
        SetJsonFormattingMode(JsonFormattingMode.Beautified);
    }

    private void UpdateJsonHistoryEmptyState() =>
        JsonHistoryEmptyText.Visibility = ViewModel.JsonFormatterHistory.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

    private void NotifyJsonHistoryError(string title, Exception exception) =>
        OnNotificationRequested(new UserNotification(title, exception.Message, IsError: true));

    private async void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox { SelectedItem: LanguageOption option }
            && ViewModel.SelectedLanguage?.Code != option.Code)
        {
            await ViewModel.ChangeLanguageAsync(option);
            UpdatePageTitle();
        }
    }

    private void OnModuleItemsDragCompleted(object sender, DragItemsCompletedEventArgs e) =>
        ViewModel.ModuleOrderChanged();

    private async void OnNotificationRequested(UserNotification notification)
    {
        if (RootNavigation.XamlRoot is null)
        {
            return;
        }

        if (notification.AutoDismiss)
        {
            await ShowTransientNotificationAsync(notification);
            return;
        }

        await _dialogGate.WaitAsync();
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = notification.Title,
                Content = notification.Message,
                CloseButtonText = ViewModel.IsEnglish ? "OK" : "确定",
                DefaultButton = ContentDialogButton.Close,
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private async Task ShowTransientNotificationAsync(UserNotification notification)
    {
        _transientNotificationCancellation?.Cancel();
        _transientNotificationCancellation?.Dispose();
        var cancellation = new CancellationTokenSource();
        _transientNotificationCancellation = cancellation;
        TransientNotificationBar.Title = notification.Title;
        TransientNotificationBar.Message = notification.Message;
        TransientNotificationBar.Severity = notification.IsError
            ? InfoBarSeverity.Error
            : InfoBarSeverity.Success;
        TransientNotificationBar.IsOpen = true;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2.5), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer transient notification replaced this one.
        }
        finally
        {
            if (ReferenceEquals(_transientNotificationCancellation, cancellation))
            {
                TransientNotificationBar.IsOpen = false;
                _transientNotificationCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task<(bool Confirmed, bool DeleteData)> ConfirmUninstallAsync(
        RuntimeKind kind,
        string version)
    {
        if (RootNavigation.XamlRoot is null)
        {
            return (false, false);
        }

        await _dialogGate.WaitAsync();
        try
        {
            CheckBox? deleteDataCheckBox = null;
            object content = ViewModel.IsEnglish
                ? $"{GetRuntimeName(kind)}@{version} will be permanently removed."
                : $"将永久卸载 {GetRuntimeName(kind)}@{version}。";
            if (kind is RuntimeKind.Redis or RuntimeKind.MySql)
            {
                deleteDataCheckBox = new CheckBox
                {
                    Content = ViewModel.IsEnglish
                        ? $"Also permanently delete this {GetRuntimeName(kind)} release line's data, configuration, credentials, and logs"
                        : $"同时永久删除此 {GetRuntimeName(kind)} 版本线的数据、配置、凭据和日志",
                    IsChecked = false,
                };
                var panel = new StackPanel { Spacing = 12 };
                panel.Children.Add(new TextBlock
                {
                    Text = (string)content,
                    TextWrapping = TextWrapping.Wrap,
                });
                panel.Children.Add(deleteDataCheckBox);
                content = panel;
            }

            var dialog = new ContentDialog
            {
                XamlRoot = RootNavigation.XamlRoot,
                Title = ViewModel.IsEnglish ? "Uninstall runtime?" : "确认卸载？",
                Content = content,
                PrimaryButtonText = ViewModel.IsEnglish ? "Uninstall" : "卸载",
                CloseButtonText = ViewModel.IsEnglish ? "Cancel" : "取消",
                DefaultButton = ContentDialogButton.Close,
            };
            var confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary;
            return (confirmed, confirmed && deleteDataCheckBox?.IsChecked == true);
        }
        finally
        {
            _dialogGate.Release();
        }
    }

    private void OnModulePreferencesChanged() => ApplyRuntimeNavigationOrder();

    private void ApplyRuntimeNavigationOrder()
    {
        RootNavigation.MenuItems.Clear();
        foreach (var kind in ViewModel.GetOrderedModuleKinds())
        {
            RootNavigation.MenuItems.Add(kind switch
            {
                ModuleKind.Node => NodeNavigationItem,
                ModuleKind.Java => JavaNavigationItem,
                ModuleKind.Python => PythonNavigationItem,
                ModuleKind.Redis => RedisNavigationItem,
                ModuleKind.MySql => MySqlNavigationItem,
                ModuleKind.Git => GitBashNavigationItem,
                ModuleKind.Toolbox => ToolboxNavigationItem,
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            });
        }

        RootNavigation.MenuItems.Add(RuntimeNavigationSeparator);
        RootNavigation.MenuItems.Add(TasksNavigationItem);
    }

    private void UpdatePageTitle()
    {
        PageTitle.Text = _currentTag switch
        {
            "tasks" => ViewModel.TaskHistoryText,
            "settings" => ViewModel.SettingsText,
            "toolbox" => ViewModel.ToolboxText,
            "git" => "Git",
            "runtime:java" => "Java",
            "runtime:python" => "Python",
            "runtime:redis" => "Redis",
            "runtime:mysql" => "MySQL",
            _ => "Node.js",
        };
    }

    private NavigationViewItem GetInitialNavigationItem()
    {
        foreach (var kind in ViewModel.GetOrderedModuleKinds())
        {
            if (!ViewModel.IsModuleEnabled(kind))
            {
                continue;
            }

            return kind switch
            {
                ModuleKind.Node => NodeNavigationItem,
                ModuleKind.Java => JavaNavigationItem,
                ModuleKind.Python => PythonNavigationItem,
                ModuleKind.Redis => RedisNavigationItem,
                ModuleKind.MySql => MySqlNavigationItem,
                ModuleKind.Git => GitBashNavigationItem,
                ModuleKind.Toolbox => ToolboxNavigationItem,
                _ => SettingsNavigationItem,
            };
        }

        return SettingsNavigationItem;
    }

    private static string GetRuntimeName(RuntimeKind kind) => kind switch
    {
        RuntimeKind.Node => "Node.js",
        RuntimeKind.Java => "Java",
        RuntimeKind.Python => "Python",
        RuntimeKind.Redis => "Redis",
        RuntimeKind.MySql => "MySQL",
        _ => kind.ToString(),
    };

    private static bool TryGetRuntimeKind(string tag, out RuntimeKind kind)
    {
        kind = tag switch
        {
            "runtime:node" => RuntimeKind.Node,
            "runtime:java" => RuntimeKind.Java,
            "runtime:python" => RuntimeKind.Python,
            "runtime:redis" => RuntimeKind.Redis,
            "runtime:mysql" => RuntimeKind.MySql,
            _ => default,
        };
        return tag is "runtime:node" or "runtime:java" or "runtime:python" or "runtime:redis" or "runtime:mysql";
    }

    private static bool IsOfficialRuntimeUri(RuntimeKind kind, Uri uri)
    {
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var officialDomain = kind switch
        {
            RuntimeKind.Node => "nodejs.org",
            RuntimeKind.Java => "github.com",
            RuntimeKind.Python => "python.org",
            RuntimeKind.Redis => "github.com",
            RuntimeKind.MySql => "mysql.com",
            _ => string.Empty,
        };
        return string.Equals(uri.Host, officialDomain, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith($".{officialDomain}", StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class PathEnvironmentEntry(string value, bool isEnglish)
{
    public string Value { get; set; } = value;

    private bool IsEnglish { get; } = isEnglish;

    public override string ToString() => string.IsNullOrEmpty(Value)
        ? IsEnglish ? "(empty entry)" : "（空条目）"
        : Value;
}
