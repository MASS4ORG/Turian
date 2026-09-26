namespace Turian.Editor.Core;

/// <summary>A pointer button that can drive a viewport gesture.</summary>
public enum ViewportButton
{
    /// <summary>Picks, drives gizmos, and orbits with Alt.</summary>
    Left,

    /// <summary>Pans the view.</summary>
    Middle,

    /// <summary>Looks around and flies with the movement keys.</summary>
    Right,
}

/// <summary>What the pointer gesture did during one frame.</summary>
public enum ViewportGesturePhase
{
    /// <summary>No button is held and none was released.</summary>
    None,

    /// <summary>A button went down and now owns the gesture.</summary>
    Pressed,

    /// <summary>The owning button is still held.</summary>
    Dragged,

    /// <summary>The owning button went up; <see cref="ViewportGesture.Released"/> names it.</summary>
    Released,
}

/// <summary>
/// Tracks which pointer button owns the current viewport drag. Only the owning button is polled until it lets go,
/// because UI toolkits often key drag state by element rather than by button, and polling another button would end
/// the running drag.
/// </summary>
public sealed class ViewportGesture
{
    /// <summary>The button that owns the gesture, or null when none is held.</summary>
    public ViewportButton? Active { get; private set; }

    /// <summary>The button released this frame, or null.</summary>
    public ViewportButton? Released { get; private set; }

    /// <summary>Advances one frame.</summary>
    /// <param name="isHeld">Whether a button is held this frame. Right is asked first, then middle, then left.</param>
    /// <returns>What the gesture did this frame.</returns>
    public ViewportGesturePhase Update(Func<ViewportButton, bool> isHeld)
    {
        ArgumentNullException.ThrowIfNull(isHeld);

        var previous = Active;
        Active = previous is { } held ? isHeld(held) ? held : null : FirstHeld(isHeld);
        Released = Active is null ? previous : null;

        return (previous, Active) switch
        {
            (null, not null) => ViewportGesturePhase.Pressed,
            (not null, not null) => ViewportGesturePhase.Dragged,
            (not null, null) => ViewportGesturePhase.Released,
            _ => ViewportGesturePhase.None,
        };
    }

    static ViewportButton? FirstHeld(Func<ViewportButton, bool> isHeld) =>
        isHeld(ViewportButton.Right) ? ViewportButton.Right
        : isHeld(ViewportButton.Middle) ? ViewportButton.Middle
        : isHeld(ViewportButton.Left) ? ViewportButton.Left
        : null;
}
