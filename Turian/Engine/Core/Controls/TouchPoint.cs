namespace Turian.Engine.Core;

/// <summary>Where a finger is in its contact with the screen during the current frame.</summary>
public enum TouchPhase
{
    /// <summary>The finger touched the screen this frame.</summary>
    Began,

    /// <summary>The finger moved this frame.</summary>
    Moved,

    /// <summary>The finger stayed down without moving this frame.</summary>
    Stationary,

    /// <summary>The finger lifted this frame.</summary>
    Ended,

    /// <summary>The system took the gesture away, so the contact ends without being a release.</summary>
    Canceled,
}

/// <summary>One finger on the screen as seen by the current frame.</summary>
/// <param name="Id">Identifies this finger for the whole contact; a later contact may reuse it.</param>
/// <param name="Phase">Where the contact is this frame.</param>
/// <param name="Position">The position in viewport pixels, with the origin at the top-left.</param>
/// <param name="Delta">The movement in pixels accumulated during the frame.</param>
/// <param name="Pressure">The reported pressure, typically 0 to 1.</param>
public readonly record struct TouchPoint(int Id, TouchPhase Phase, Vector2 Position, Vector2 Delta, float Pressure);
