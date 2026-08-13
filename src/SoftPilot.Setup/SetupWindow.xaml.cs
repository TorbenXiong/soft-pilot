using System.Windows;
using System.IO;
using System.Windows.Media;
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
        var defaultParent = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs");
        UpdateSelection(defaultParent);
    }

    private void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择 SoftPilot 的父目录",
            InitialDirectory = ParentPathBox.Text,
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

        var answer = System.Windows.MessageBox.Show(
            this,
            $"SoftPilot 将安装到：\n\n{_validation.FinalRoot}\n\n覆盖升级只替换 bin；V1 不支持安装后迁移。",
            "确认最终安装位置",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Information,
            MessageBoxResult.Cancel);
        if (answer != MessageBoxResult.OK)
        {
            return;
        }

        SetInstalling(true);
        try
        {
            var progress = new Progress<InstallProgress>(value =>
            {
                ProgressText.Text = value.Message;
                InstallProgress.Value = value.Percentage;
            });
            await _installer.InstallAsync(_validation.FinalRoot, progress);
            System.Windows.MessageBox.Show(
                this,
                $"SoftPilot 已安装到：\n{_validation.FinalRoot}",
                "安装完成",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
        ParentPathBox.Text = _validation.SelectedParent;
        FinalPathText.Text = _validation.FinalRoot;
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
        InstallProgress.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        if (!installing)
        {
            ProgressText.Text = string.Empty;
        }
    }
}
