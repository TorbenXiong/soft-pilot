using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoftPilot.Domain;
using SoftPilot.Gui.ViewModels;

namespace SoftPilot.Gui;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        _ = InitializeAsync();
    }

    public MainViewModel ViewModel { get; }

    private async Task InitializeAsync()
    {
        await ViewModel.RefreshAsync();
        RootNavigation.SelectedItem = GetInitialNavigationItem();
    }

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is not string tag)
        {
            return;
        }

        var isRuntime = tag.StartsWith("runtime:", StringComparison.Ordinal);
        RuntimesView.Visibility = isRuntime ? Visibility.Visible : Visibility.Collapsed;
        TasksView.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        SettingsView.Visibility = tag == "settings" ? Visibility.Visible : Visibility.Collapsed;

        if (isRuntime && TryGetRuntimeKind(tag, out var kind))
        {
            ViewModel.SelectRuntimeModule(kind);
        }

        (PageTitle.Text, PageSubtitle.Text) = tag switch
        {
            "tasks" => ("任务历史", "查看安装、卸载、恢复与失败原因。"),
            "settings" => ("设置", "配置模块、工作区信息与显式 Shell 集成。"),
            "runtime:java" => ("Java", "管理 Eclipse Temurin JDK 的安装版本与全局切换。"),
            "runtime:python" => ("Python", "管理 CPython 的安装版本与全局切换。"),
            _ => ("Node.js", "管理 Node.js 的安装版本与全局切换。"),
        };
    }

    private NavigationViewItem GetInitialNavigationItem()
    {
        if (ViewModel.NodeModuleEnabled)
        {
            return NodeNavigationItem;
        }

        if (ViewModel.JavaModuleEnabled)
        {
            return JavaNavigationItem;
        }

        if (ViewModel.PythonModuleEnabled)
        {
            return PythonNavigationItem;
        }

        return SettingsNavigationItem;
    }

    private static bool TryGetRuntimeKind(string tag, out RuntimeKind kind)
    {
        kind = tag switch
        {
            "runtime:node" => RuntimeKind.Node,
            "runtime:java" => RuntimeKind.Java,
            "runtime:python" => RuntimeKind.Python,
            _ => default,
        };
        return tag is "runtime:node" or "runtime:java" or "runtime:python";
    }
}
