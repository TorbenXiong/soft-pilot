using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace SoftPilot.Gui.Controls;

public sealed class StableHyperlinkButton : HyperlinkButton
{
    public StableHyperlinkButton()
    {
        ProtectedCursor = StableCursorResources.Hand;
    }
}

public sealed class StableCursorButton : Button
{
    public StableCursorButton()
    {
        ProtectedCursor = StableCursorResources.Hand;
    }
}

public sealed class StableCursorToggleButton : ToggleButton
{
    public StableCursorToggleButton()
    {
        ProtectedCursor = StableCursorResources.Hand;
    }
}

public sealed class StableCursorContentPresenter : ContentPresenter
{
    public StableCursorContentPresenter()
    {
        ProtectedCursor = StableCursorResources.Hand;
    }
}

internal static class StableCursorResources
{
    internal static InputCursor Hand { get; } =
        InputSystemCursor.Create(InputSystemCursorShape.Hand);
}
