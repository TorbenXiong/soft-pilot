using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SoftPilot.Application.Abstractions;

namespace SoftPilot.Gui.ViewModels;

public partial class CocosCreatorVersionRow : ObservableObject
{
    public CocosCreatorVersionRow(
        CocosCreatorRelease? release,
        CocosCreatorInstallationStatus? installation,
        string installDirectory,
        bool isUpgrade,
        bool isEnglish)
    {
        Release = release;
        Version = release?.Version ?? installation?.Version
            ?? throw new ArgumentException("Creator row requires a release or installation.");
        InstallDirectory = installDirectory;
        LauncherPath = installation?.LauncherPath
            ?? Path.Combine(installDirectory, "CocosCreator.exe");
        IsInstalled = installation is not null;
        IsUpgrade = isUpgrade;
        Problem = installation?.Problem;
        InstallText = isEnglish ? "Install" : "安装";
        LaunchText = isEnglish ? "Launch" : "启动";
        UninstallText = isEnglish ? "Uninstall" : "卸载";
        StatusText = installation is null
            ? isEnglish ? "Available" : "可安装"
            : installation.IsHealthy
                ? isEnglish ? "Installed" : "已安装"
                : isEnglish ? "Damaged" : "异常";
    }

    public CocosCreatorRelease? Release { get; }
    public string Version { get; }
    public string InstallDirectory { get; }
    public string LauncherPath { get; }
    public bool IsInstalled { get; }
    public bool IsUpgrade { get; }
    public string? Problem { get; }
    public string InstallText { get; }
    public string LaunchText { get; }
    public string UninstallText { get; }
    public string StatusText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(CanLaunch))]
    [NotifyPropertyChangedFor(nameof(CanUninstall))]
    [NotifyPropertyChangedFor(nameof(ProgressVisibility))]
    [NotifyPropertyChangedFor(nameof(FeedbackVisibility))]
    [NotifyPropertyChangedFor(nameof(ActionsVisibility))]
    public partial bool IsOperating { get; set; }

    [ObservableProperty]
    public partial double OperationPercentage { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FeedbackVisibility))]
    public partial string OperationText { get; set; } = string.Empty;

    public bool CanInstall => !IsInstalled && Release is not null && !IsOperating;
    public bool CanLaunch => IsInstalled && string.IsNullOrWhiteSpace(Problem) && !IsOperating;
    public bool CanUninstall => IsInstalled && !IsOperating;
    public Visibility InstallVisibility => !IsInstalled && Release is not null
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility InstalledActionsVisibility => IsInstalled
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ProblemVisibility => string.IsNullOrWhiteSpace(Problem)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility ProgressVisibility => IsOperating
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility ActionsVisibility => IsOperating
        ? Visibility.Collapsed
        : Visibility.Visible;
    public Visibility FeedbackVisibility => !IsOperating && !string.IsNullOrWhiteSpace(OperationText)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Brush StatusBrush => string.IsNullOrWhiteSpace(Problem)
        ? new SolidColorBrush(Microsoft.UI.Colors.ForestGreen)
        : new SolidColorBrush(Microsoft.UI.Colors.IndianRed);
}
