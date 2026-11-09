using TouchPhase = Turian.Engine.Core.TouchPhase;

namespace Turian.Tests;

/// <summary>Tests the touch contract of <see cref="BufferedInputSource"/> and its bindings, through a fake event stream.</summary>
public class TouchInputTests
{
    static readonly Vector2 Start = new(10, 20);
    static readonly Vector2 End = new(40, 60);

    /// <summary>A contact runs through its phases, and its movement is reported for one frame.</summary>
    [Fact]
    public void ContactReportsPhasesAndFrameDelta()
    {
        var input = new BufferedInputSource();

        input.PushTouchDown(7, Start, 0.5f);
        Assert.Equal(new TouchPoint(7, TouchPhase.Began, Start, Vector2.Zero, 0.5f), Assert.Single(input.Touches));

        input.NewFrame();
        Assert.Equal(TouchPhase.Stationary, Assert.Single(input.Touches).Phase);

        input.PushTouchMove(7, End);
        var moved = Assert.Single(input.Touches);
        Assert.Equal(TouchPhase.Moved, moved.Phase);
        Assert.Equal(End - Start, moved.Delta);

        input.NewFrame();
        Assert.Equal(Vector2.Zero, Assert.Single(input.Touches).Delta);

        input.PushTouchUp(7);
        Assert.Equal(TouchPhase.Ended, Assert.Single(input.Touches).Phase);

        input.NewFrame();
        Assert.Empty(input.Touches);
    }

    /// <summary>Touches stay in contact order so a binding's finger index is stable.</summary>
    [Fact]
    public void FingersKeepContactOrder()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(3, Start);
        input.PushTouchDown(1, End);

        Assert.Equal([3, 1], input.Touches.Select(static touch => touch.Id));
    }

    /// <summary>A tap shorter than a frame is seen as down through a tick and the interface pass that follows it.</summary>
    [Fact]
    public void SubFrameTapStaysVisibleToGameplayAndInterface()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Start);
        input.PushTouchUp(1);

        Assert.Equal(TouchPhase.Began, Assert.Single(input.Touches).Phase);
        Assert.True(input.WasMouseButtonPressed(SilkMouseButton.Left));

        input.NewFrame();
        Assert.True(input.IsMouseButtonDown(SilkMouseButton.Left));

        input.NewFrame();
        Assert.Equal(TouchPhase.Ended, Assert.Single(input.Touches).Phase);
        Assert.False(input.IsMouseButtonDown(SilkMouseButton.Left));
        Assert.True(input.WasMouseButtonReleased(SilkMouseButton.Left));

        input.NewFrame();
        Assert.Empty(input.Touches);
    }

    /// <summary>The first finger drives the emulated pointer, so a pointer interface receives a press and a release.</summary>
    [Fact]
    public void PrimaryTouchEmulatesPointerEvents()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Start);

        Assert.Equal(Start, input.MousePosition);
        Assert.Equal(Vector2.Zero, input.MouseDelta);
        Assert.True(input.WasMouseButtonPressed(SilkMouseButton.Left));

        input.NewFrame();
        input.PushTouchMove(1, End);
        Assert.Equal(End, input.MousePosition);
        Assert.Equal(End - Start, input.MouseDelta);

        input.NewFrame();
        input.PushTouchUp(1);
        Assert.False(input.IsMouseButtonDown(SilkMouseButton.Left));
        Assert.True(input.WasMouseButtonReleased(SilkMouseButton.Left));
    }

    /// <summary>A second finger leaves the emulated pointer alone.</summary>
    [Fact]
    public void SecondFingerDoesNotMovePointer()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Start);
        input.NewFrame();
        input.PushTouchDown(2, End);
        input.PushTouchMove(2, End * 2);

        Assert.Equal(Start, input.MousePosition);
        Assert.True(input.IsMouseButtonDown(SilkMouseButton.Left));
    }

    /// <summary>A gesture the system takes away must not click what is under the finger.</summary>
    [Fact]
    public void CancelReleasesPointerWithoutAReleaseEdge()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Start);
        input.NewFrame();

        input.PushTouchCancel(1);

        Assert.Equal(TouchPhase.Canceled, Assert.Single(input.Touches).Phase);
        Assert.False(input.IsMouseButtonDown(SilkMouseButton.Left));
        Assert.False(input.WasMouseButtonReleased(SilkMouseButton.Left));
    }

    /// <summary>With emulation off, fingers never reach the mouse.</summary>
    [Fact]
    public void EmulationCanBeDisabled()
    {
        var input = new BufferedInputSource { TouchEmulatesMouse = false };
        input.PushTouchDown(1, Start);

        Assert.Equal(Vector2.Zero, input.MousePosition);
        Assert.False(input.IsMouseButtonDown(SilkMouseButton.Left));
    }

    /// <summary>Losing focus drops every contact and the emulated button.</summary>
    [Fact]
    public void ClearDropsContacts()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Start);

        input.Clear();

        Assert.Empty(input.Touches);
        Assert.False(input.IsMouseButtonDown(SilkMouseButton.Left));
    }

    /// <summary>Touch controls round-trip through binding paths and read the matching finger.</summary>
    [Theory]
    [InlineData("Touch/Primary", InputControlKind.TouchButton, false)]
    [InlineData("Touch/Secondary", InputControlKind.TouchButton, false)]
    [InlineData("Touch/DeltaX", InputControlKind.TouchAxis, true)]
    [InlineData("Touch/DeltaY", InputControlKind.TouchAxis, true)]
    public void BindingPathsRoundTrip(string path, InputControlKind kind, bool axis)
    {
        var control = InputControl.Parse(path);

        Assert.Equal(kind, control.Kind);
        Assert.Equal(axis, control.IsAxis);
        Assert.Equal(path, control.ToString());
    }

    /// <summary>A finger binding reads held until the contact ends, and its press edge lasts one frame.</summary>
    [Fact]
    public void FingerBindingReadsHeldAndEdge()
    {
        var input = new BufferedInputSource();
        var primary = InputControl.Touch(TouchButton.Primary);
        var secondary = InputControl.Touch(TouchButton.Secondary);

        Assert.Equal(0f, input.Read(primary));

        input.PushTouchDown(1, Start);
        Assert.Equal(1f, input.Read(primary));
        Assert.Equal(0f, input.Read(secondary));
        Assert.True(input.WentDown(primary));

        input.NewFrame();
        Assert.Equal(1f, input.Read(primary));
        Assert.False(input.WentDown(primary));

        input.PushTouchUp(1);
        Assert.Equal(0f, input.Read(primary));
    }

    /// <summary>The motion axes read the first finger's movement this frame.</summary>
    [Fact]
    public void MotionAxesReadFirstFinger()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Start);
        input.NewFrame();
        input.PushTouchMove(1, End);

        Assert.Equal(End.X - Start.X, input.Read(InputControl.TouchMotion(TouchAxis.DeltaX)));
        Assert.Equal(End.Y - Start.Y, input.Read(InputControl.TouchMotion(TouchAxis.DeltaY)));
    }

    /// <summary>A source without touch support reports none, leaving the desktop contract unchanged.</summary>
    [Fact]
    public void DefaultSourceReportsNoTouches()
    {
        IInputSource source = new SilentSource();

        Assert.Empty(source.Touches);
        Assert.Equal(0f, source.Read(InputControl.Touch(TouchButton.Primary)));
    }

    sealed class SilentSource : IInputSource
    {
        public Vector2 MousePosition => default;

        public Vector2 MouseDelta => default;

        public float MouseScrollDelta => 0f;

        public bool IsKeyDown(Key key) => false;

        public bool WasKeyPressed(Key key) => false;

        public bool WasKeyReleased(Key key) => false;

        public bool IsMouseButtonDown(SilkMouseButton button) => false;

        public bool WasMouseButtonPressed(SilkMouseButton button) => false;

        public bool WasMouseButtonReleased(SilkMouseButton button) => false;

        public void NewFrame()
        {
        }
    }
}
