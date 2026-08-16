using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace SoftPilot.Gui;

internal static class WindowPositioning
{
    public static void CenterOnPrimaryDisplay(AppWindow appWindow)
    {
        var displayArea = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var size = appWindow.Size;
        var x = workArea.X + Math.Max(0, (workArea.Width - size.Width) / 2);
        var y = workArea.Y + Math.Max(0, (workArea.Height - size.Height) / 2);
        appWindow.Move(new PointInt32(x, y));
    }
}
