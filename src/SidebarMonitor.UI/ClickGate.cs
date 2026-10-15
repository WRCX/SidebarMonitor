namespace SidebarMonitor.UI;

/// <summary>
/// A click is a press AND a release on the same element. WPF raises MouseLeftButtonUp for any
/// release over an element, wherever the button went down, so a handler on Up alone also fires for
/// releases nobody aimed at it.
/// <para>
/// That is how the collapsed panel kept reopening on an RDP connect or disconnect: the same UI
/// process, no restart, and Minimized=false written back to ui.json as if the user had clicked the
/// tab. The tab's Up handler is the only in-process path that reopens the panel, and the tab sits on
/// the screen edge — where the cursor lands when the resolution changes under it — so a release with
/// no matching press, of the kind input resynchronisation around a session change delivers, was
/// enough. Only a release that follows a press on the element counts now.
/// </para>
/// Pure state, kept apart from the WPF wiring so it can be unit-tested without a dispatcher.
/// </summary>
internal sealed class ClickGate
{
    private bool _pressed;

    /// <summary>The button went down on the element.</summary>
    public void Press() => _pressed = true;

    /// <summary>The pointer left the element: a release elsewhere is not a click on it, and the
    /// press must not linger to legitimise a later stray release.</summary>
    public void Leave() => _pressed = false;

    /// <summary>The button came up over the element. True only when it went down there too.</summary>
    public bool Release()
    {
        bool wasPressed = _pressed;
        _pressed = false;
        return wasPressed;
    }
}
