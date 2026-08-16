using System.Runtime.InteropServices;

namespace SoftPilot.Gui;

internal static class DesktopShortcutService
{
    public static void Create(string targetExecutable)
    {
        targetExecutable = Path.GetFullPath(targetExecutable);
        if (!File.Exists(targetExecutable))
        {
            throw new FileNotFoundException("找不到快捷方式目标。", targetExecutable);
        }

        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (string.IsNullOrWhiteSpace(desktopDirectory))
        {
            throw new InvalidOperationException("无法确定当前用户的桌面目录。");
        }

        Directory.CreateDirectory(desktopDirectory);
        var shortcutPath = Path.Combine(desktopDirectory, "SoftPilot.lnk");
        var shellType = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("系统不支持创建桌面快捷方式。");
        object? shell = null;
        object? shortcut = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("无法创建 WScript.Shell 对象。");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(shortcutPath);
            dynamic dynamicShortcut = shortcut;
            dynamicShortcut.TargetPath = targetExecutable;
            dynamicShortcut.WorkingDirectory = Path.GetDirectoryName(targetExecutable)!;
            dynamicShortcut.IconLocation = $"{targetExecutable},0";
            dynamicShortcut.Description = "SoftPilot";
            dynamicShortcut.Save();
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            _ = Marshal.FinalReleaseComObject(value);
        }
    }
}
