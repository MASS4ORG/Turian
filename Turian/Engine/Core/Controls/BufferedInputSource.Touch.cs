namespace Turian.Engine.Core;

/// <summary>Touch tracking and the emulated mouse driven by the first finger.</summary>
public sealed partial class BufferedInputSource
{
    readonly List<ActiveTouch> touches = [];
    int? primaryTouch;

    /// <summary>Whether the first finger also drives the mouse, so pointer-based interfaces work on a touch screen.</summary>
    public bool TouchEmulatesMouse { get; set; } = true;

    /// <inheritdoc/>
    public IReadOnlyList<TouchPoint> Touches
    {
        get
        {
            lock (gate)
            {
                return [.. touches.Select(static touch => touch.Snapshot())];
            }
        }
    }

    /// <summary>Records that a finger touched the screen.</summary>
    /// <param name="id">The finger's identifier for this contact.</param>
    /// <param name="position">The position in viewport pixels.</param>
    /// <param name="pressure">The reported pressure.</param>
    public void PushTouchDown(int id, Vector2 position, float pressure = 1f)
    {
        lock (gate)
        {
            touches.RemoveAll(touch => touch.Id == id);
            touches.Add(new ActiveTouch(id, position, pressure));
            if (primaryTouch is null && TouchEmulatesMouse)
            {
                primaryTouch = id;
                mousePosition = position;
                if (buttonsDown.Add(MouseButton.Left))
                {
                    buttonsPressed.Add(MouseButton.Left);
                }
            }
        }
    }

    /// <summary>Records a finger's new position.</summary>
    /// <param name="id">The finger's identifier for this contact.</param>
    /// <param name="position">The position in viewport pixels.</param>
    /// <param name="pressure">The reported pressure.</param>
    public void PushTouchMove(int id, Vector2 position, float pressure = 1f)
    {
        lock (gate)
        {
            if (Find(id) is not { } touch)
            {
                return;
            }

            touch.Delta += position - touch.Position;
            touch.Position = position;
            touch.Pressure = pressure;
            if (touch.Phase != TouchPhase.Began)
            {
                touch.Phase = TouchPhase.Moved;
            }

            if (primaryTouch == id)
            {
                mouseDelta += position - mousePosition;
                mousePosition = position;
            }
        }
    }

    /// <summary>Records that a finger lifted.</summary>
    /// <param name="id">The finger's identifier for this contact.</param>
    public void PushTouchUp(int id) => EndTouch(id, TouchPhase.Ended);

    /// <summary>Records that the system took a finger's gesture away, which must not read as a release.</summary>
    /// <param name="id">The finger's identifier for this contact.</param>
    public void PushTouchCancel(int id) => EndTouch(id, TouchPhase.Canceled);

    void EndTouch(int id, TouchPhase phase)
    {
        lock (gate)
        {
            if (Find(id) is not { } touch)
            {
                return;
            }

            // A contact shorter than a frame still has to be seen as down by gameplay and by the interface.
            if (touch.Phase == TouchPhase.Began)
            {
                touch.PendingEnd = phase;
                return;
            }

            touch.Phase = phase;
            ReleasePrimary(touch);
        }
    }

    void ReleasePrimary(ActiveTouch touch)
    {
        if (primaryTouch != touch.Id)
        {
            return;
        }

        primaryTouch = null;
        if (buttonsDown.Remove(MouseButton.Left) && touch.Phase == TouchPhase.Ended)
        {
            buttonsReleased.Add(MouseButton.Left);
        }
    }

    void RollTouches()
    {
        touches.RemoveAll(static touch => touch.Phase is TouchPhase.Ended or TouchPhase.Canceled);
        foreach (var touch in touches)
        {
            touch.Delta = Vector2.Zero;
            if (touch.PendingEnd is { } end && touch.FramesSeen > 0)
            {
                touch.Phase = end;
                touch.PendingEnd = null;
                ReleasePrimary(touch);
                continue;
            }

            touch.FramesSeen++;
            touch.Phase = TouchPhase.Stationary;
        }
    }

    ActiveTouch? Find(int id) => touches.Find(touch => touch.Id == id);

    /// <summary>One finger's contact, mutated as its events arrive.</summary>
    sealed class ActiveTouch(int id, Vector2 position, float pressure)
    {
        public int Id { get; } = id;

        public TouchPhase Phase { get; set; } = TouchPhase.Began;

        public Vector2 Position { get; set; } = position;

        public Vector2 Delta { get; set; }

        public float Pressure { get; set; } = pressure;

        public TouchPhase? PendingEnd { get; set; }

        public int FramesSeen { get; set; }

        public TouchPoint Snapshot() => new(Id, Phase, Position, Delta, Pressure);
    }
}
