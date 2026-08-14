using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;

namespace SoftPilot.Gui.Controls;

public sealed class StableHyperlinkButton : HyperlinkButton
{
    public StableHyperlinkButton()
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
