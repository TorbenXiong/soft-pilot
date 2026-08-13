using System.Windows;

namespace SoftPilot.Uninstall;

public partial class UninstallWindow : Window
{
    public UninstallWindow() => InitializeComponent();

    public bool DeleteWorkspace => DeleteWorkspaceBox.IsChecked == true;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnUninstall(object sender, RoutedEventArgs e)
    {
        if (DeleteWorkspace && MessageBox.Show(
                this,
                "这会永久删除所有 SoftPilot 管理的运行时和数据。是否继续？",
                "确认完整删除",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        DialogResult = true;
        Close();
    }
}
