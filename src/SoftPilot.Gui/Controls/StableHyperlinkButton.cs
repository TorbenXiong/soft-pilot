using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;

namespace SoftPilot.Gui.Controls;

public sealed class StableHyperlinkButton : HyperlinkButton
{
    private InputCursor? _handCursor;

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        ProtectedCursor = _handCursor ??=
            InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

public sealed class StableCursorButton : Button
{
    private InputCursor? _handCursor;

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        ProtectedCursor = _handCursor ??=
            InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

public sealed class StableCursorToggleButton : ToggleButton
{
    private InputCursor? _handCursor;

    protected override void OnPointerEntered(PointerRoutedEventArgs e)
    {
        base.OnPointerEntered(e);
        ProtectedCursor = _handCursor ??=
            InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}

public sealed class StableCursorContentPresenter : ContentPresenter
{
    private InputCursor? _handCursor;

    public StableCursorContentPresenter()
    {
        PointerEntered += OnPointerEntered;
    }

    private void OnPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        ProtectedCursor = _handCursor ??=
            InputSystemCursor.Create(InputSystemCursorShape.Hand);
    }
}
