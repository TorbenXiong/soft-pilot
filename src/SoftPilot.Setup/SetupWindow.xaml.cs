using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SoftPilot.Application.Abstractions;
using SoftPilot.Infrastructure.Installation;

namespace SoftPilot.Setup;

public partial class SetupWindow : Window
{
    private readonly IInstallationPathService _paths = new WindowsInstallationPathService();
    private readonly InstallerEngine _installer = new();
    private InstallationPathValidation? _validation;

    public SetupWindow()
    {
        InitializeComponent();
        UpdateSelection(_paths.GetDefaultParentDirectory());
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 SoftPilot 的父目录",
            InitialDirectory = _validation?.SelectedParent ?? string.Empty,
            Multiselect = false,
        };
        if (dialog.ShowDialog(this) == true)
        {
            UpdateSelection(dialog.FolderName);
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (_validation is not { IsValid: true })
        {
            return;
        }

        var launchAfterInstall = LaunchAfterInstallBox.IsChecked == true;
        SetInstalling(true);
        try
        {
            var progress = new Progress<InstallProgress>(value =>
            {
                ProgressText.Text = value.Message;
                InstallProgress.Value = value.Percentage;
            });
            await _installer.InstallAsync(_validation.FinalRoot, progress);
            if (launchAfterInstall)
            {
                LaunchInstalledApp(_validation.FinalRoot);
            }

            Close();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "安装失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            SetInstalling(false);
        }
    }

    private void UpdateSelection(string parent)
    {
        _validation = _paths.Validate(parent);
        InstallPathBox.Text = _validation.FinalRoot;
        ValidationText.Text = _validation.IsValid
            ? "位置有效：本地固定 NTFS 磁盘，当前用户可写。"
            : string.Join(Environment.NewLine, _validation.Errors.Select(error => $"• {error}"));
        ValidationText.Foreground = _validation.IsValid
            ? System.Windows.Media.Brushes.ForestGreen
            : System.Windows.Media.Brushes.Firebrick;
        InstallButton.IsEnabled = _validation.IsValid;
    }

    private void SetInstalling(bool installing)
    {
        InstallButton.IsEnabled = !installing && _validation is { IsValid: true };
        ChooseFolderButton.IsEnabled = !installing;
        LaunchAfterInstallBox.IsEnabled = !installing;
        ProgressText.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        InstallProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        if (!installing)
        {
            ProgressText.Text = string.Empty;
        }
    }

    private void LaunchInstalledApp(string root)
    {
        var executable = Path.Combine(root, "bin", "SoftPilot.exe");
        try
        {
            Process.Start(new ProcessStartInfo(executable) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                $"SoftPilot 已安装，但未能自动启动：{exception.Message}\n\n请从 Windows 开始菜单搜索“SoftPilot”。",
                "无法启动 SoftPilot",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }
}
