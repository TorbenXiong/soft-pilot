using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SoftPilot.Application.Abstractions;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace SoftPilot.Gui;

public sealed partial class WorkspaceSetupWindow : Window
{
    private static readonly (string Chinese, string English)[] ValidationMessages =
    [
        ("路径无效：", "Invalid path: "),
        ("必须选择本地绝对路径，不能使用 UNC 或网络路径。", "Choose a local absolute path; UNC and network paths are not supported."),
        ("无法确定目标磁盘。", "The target drive could not be determined."),
        ("目标磁盘尚未就绪。", "The target drive is not ready."),
        ("目标必须位于本地固定磁盘，不能使用网络盘或可移动盘。", "The target must be on a local fixed drive, not a network or removable drive."),
        ("目标磁盘必须使用 NTFS 文件系统。", "The target drive must use the NTFS file system."),
        ("无法读取目标磁盘信息：", "Unable to read target drive information: "),
        ("最终目录不能位于 Windows、Program Files 或 ProgramData 等系统管理目录中。", "The final directory cannot be inside a system-managed directory such as Windows, Program Files, or ProgramData."),
        ("最终目录不能位于已知云同步目录中。", "The final directory cannot be inside a known cloud-synced directory."),
        ("最终路径已被同名文件占用。", "A file already occupies the final path."),
        ("最终目录非空且不属于 SoftPilot，可能已被其他应用占用。", "The final directory is not empty and does not belong to SoftPilot."),
        ("找不到可写的父目录。", "No writable parent directory was found."),
        ("当前用户无法写入目标目录：", "The current user cannot write to the target directory: "),
    ];

    private readonly IInstallationPathService _paths;
    private readonly TaskCompletionSource<string?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private InstallationPathValidation? _validation;
    private bool _completed;
    private bool _isMigrating;
    private bool _allowClose;

    public WorkspaceSetupWindow(IInstallationPathService paths)
    {
        _paths = paths;
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(780, 500));
        WindowPositioning.CenterOnPrimaryDisplay(AppWindow);
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;
        LanguageBox.SelectedIndex = 0;
        ApplyLanguage();
        UpdateSelection(_paths.GetDefaultParentDirectory());
    }

    public Task<string?> WaitForSelectionAsync() => _completion.Task;

    public bool CreateDesktopShortcut => CreateDesktopShortcutCheckBox.IsChecked == true;

    public string SelectedLanguageCode { get; private set; } = "zh-CN";

    private bool IsEnglish => SelectedLanguageCode == "en-US";

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            UpdateSelection(folder.Path);
        }
    }

    private void OnContinue(object sender, RoutedEventArgs e)
    {
        if (_validation is not { IsValid: true } || _isMigrating)
        {
            return;
        }

        _completed = true;
        _isMigrating = true;
        ChooseFolderButton.IsEnabled = false;
        CreateDesktopShortcutCheckBox.IsEnabled = false;
        ContinueButton.IsEnabled = false;
        ValidationText.Text = T("正在迁移并启动 SoftPilot，请稍候…", "Moving and starting SoftPilot. Please wait…");
        ValidationText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Gray);
        _completion.TrySetResult(_validation.FinalRoot);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object sender, WindowEventArgs e)
    {
        if (!_completed)
        {
            _completion.TrySetResult(null);
        }
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_isMigrating && !_allowClose)
        {
            args.Cancel = true;
        }
    }

    public void CloseAfterSubmission()
    {
        _allowClose = true;
        Close();
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language })
        {
            return;
        }

        SelectedLanguageCode = language;
        ApplyLanguage();
        RenderValidation();
    }

    private void ApplyLanguage()
    {
        Title = T("SoftPilot 首次设置", "SoftPilot first-time setup");
        WelcomeText.Text = T("欢迎使用 SoftPilot", "Welcome to SoftPilot");
        FirstUseText.Text = T(
            "首次使用只需指定工作区，SoftPilot 会迁移到最终目录并重新启动。",
            "Choose a workspace once. SoftPilot will move to its final location and restart.");
        UpgradeText.Text = T(
            "升级时请先退出 SoftPilot，再用新版 SoftPilot.exe 替换原文件。",
            "To upgrade, exit SoftPilot and replace the existing SoftPilot.exe with the new version.");
        LocationText.Text = T("应用与工作区位置", "App and workspace location");
        ChooseFolderButton.Content = T("选择文件夹", "Choose folder");
        CreateDesktopShortcutCheckBox.Content = T("创建桌面快捷方式", "Create a desktop shortcut");
        CancelButton.Content = T("退出", "Exit");
        ContinueButton.Content = T("使用此位置", "Use this location");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(
            RootPathBox,
            T("SoftPilot 最终应用目录", "SoftPilot final app directory"));
    }

    private void UpdateSelection(string parent)
    {
        _validation = _paths.Validate(parent);
        RootPathBox.Text = _validation.FinalRoot;
        RenderValidation();
    }

    private void RenderValidation()
    {
        if (_validation is null)
        {
            return;
        }

        ValidationText.Text = _validation.IsValid
            ? T(
                "位置有效：本地固定 NTFS 磁盘，当前用户可写。",
                "Valid location: local fixed NTFS drive, writable by the current user.")
            : string.Join(
                Environment.NewLine,
                _validation.Errors.Select(error => $"• {LocalizeValidationError(error)}"));
        ValidationText.Foreground = _validation.IsValid
            ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.ForestGreen)
            : new Microsoft.UI.Xaml.Media.SolidColorBrush(Colors.Firebrick);
        ContinueButton.IsEnabled = _validation.IsValid;
    }

    private string LocalizeValidationError(string error)
    {
        if (!IsEnglish)
        {
            return error;
        }

        foreach (var (chinese, english) in ValidationMessages)
        {
            if (error.StartsWith(chinese, StringComparison.Ordinal))
            {
                return english + error[chinese.Length..];
            }
        }

        return error;
    }

    private string T(string chinese, string english) => IsEnglish ? english : chinese;
}
