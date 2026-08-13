using Microsoft.Win32;

namespace SoftPilot.Infrastructure.Installation;

public sealed class WindowsRootRegistry : IRootRegistry
{
    public const string KeyPath = @"Software\SoftPilot";
    public const string ValueName = "Root";

    public string? ReadRoot()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        return key?.GetValue(ValueName) as string;
    }

    public void WriteRoot(string root)
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
        key.SetValue(ValueName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), RegistryValueKind.String);
    }

    public void DeleteRoot()
    {
        Registry.CurrentUser.DeleteSubKeyTree(KeyPath, throwOnMissingSubKey: false);
    }
}
