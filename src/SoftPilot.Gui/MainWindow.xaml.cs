using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoftPilot.Domain;
using SoftPilot.Gui.ViewModels;
using Windows.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.Graphics;

namespace SoftPilot.Gui;

public sealed partial class MainWindow : Window
{
    private readonly SemaphoreSlim _dialogGate = new(1, 1);
    private string _currentTag = "runtime:node";

    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ViewModel.NotificationRequested += OnNotificationRequested;
        ViewModel.ModulePreferencesChanged += OnModulePreferencesChanged;
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
        TasksView.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

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

    private async void OnStartInstalledRedisClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledRuntimeRow row)
        {
            await ViewModel.StartInstalledRedisAsync(row);
        }
    }

    private async void OnStopInstalledRedisClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is InstalledRuntimeRow row)
        {
            await ViewModel.StopInstalledRedisAsync(row);
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

    private async void OnCopyInstalledPathClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledRuntimeRow row)
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(row.Path);
        Clipboard.SetContent(package);
        row.SetPathStatus(ViewModel.IsEnglish ? "Copied" : "已复制");
        await Task.Delay(TimeSpan.FromSeconds(2));
        row.SetPathStatus(string.Empty);
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
            if (kind == RuntimeKind.Redis)
            {
                deleteDataCheckBox = new CheckBox
                {
                    Content = ViewModel.IsEnglish
                        ? "Also permanently delete this version's Redis data and logs"
                        : "同时永久删除此版本的 Redis 数据和日志",
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
                RuntimeKind.Node => NodeNavigationItem,
                RuntimeKind.Java => JavaNavigationItem,
                RuntimeKind.Python => PythonNavigationItem,
                RuntimeKind.Redis => RedisNavigationItem,
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
            "runtime:java" => "Java",
            "runtime:python" => "Python",
            "runtime:redis" => "Redis",
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
                RuntimeKind.Node => NodeNavigationItem,
                RuntimeKind.Java => JavaNavigationItem,
                RuntimeKind.Python => PythonNavigationItem,
                RuntimeKind.Redis => RedisNavigationItem,
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
            _ => default,
        };
        return tag is "runtime:node" or "runtime:java" or "runtime:python" or "runtime:redis";
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
            _ => string.Empty,
        };
        return string.Equals(uri.Host, officialDomain, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith($".{officialDomain}", StringComparison.OrdinalIgnoreCase);
    }
}
