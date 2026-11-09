using Turian.Android;
using TouchPhase = Turian.Engine.Core.TouchPhase;

namespace Turian.Tests;

/// <summary>Translation of Android motion events into engine touch events, using the platform's action codes.</summary>
public sealed class AndroidTouchInputTests
{
    const int Down = 0;
    const int Up = 1;
    const int Move = 2;
    const int Cancel = 3;
    const int PointerDown = 5;
    const int PointerUp = 6;

    /// <summary>Only the acting pointer is pressed when a further finger lands.</summary>
    [Fact]
    public void PointerDownOnlyPressesTheActingPointer()
    {
        var input = new BufferedInputSource();
        AndroidTouchInput.Apply(input, Down, 4, [new(4, 1, 2, 0.5f)]);
        input.NewFrame();

        AndroidTouchInput.Apply(input, PointerDown, 9, [new(4, 1, 2, 0.5f), new(9, 3, 4, 1f)]);

        Assert.Equal([4, 9], input.Touches.Select(static touch => touch.Id));
        Assert.Equal(TouchPhase.Stationary, input.Touches[0].Phase);
        Assert.Equal(TouchPhase.Began, input.Touches[1].Phase);
    }

    /// <summary>A move event updates every finger in the gesture.</summary>
    [Fact]
    public void MoveUpdatesEveryPointer()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Vector2.Zero);
        input.PushTouchDown(2, Vector2.One);
        input.NewFrame();

        AndroidTouchInput.Apply(input, Move, 1, [new(1, 5, 5, 1f), new(2, 7, 7, 1f)]);

        Assert.Equal(new Vector2(5, 5), input.Touches[0].Position);
        Assert.Equal(new Vector2(7, 7), input.Touches[1].Position);
    }

    /// <summary>Lifting one finger ends only that contact.</summary>
    [Theory]
    [InlineData(PointerUp)]
    [InlineData(Up)]
    public void UpEndsTheActingPointer(int action)
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Vector2.Zero);
        input.PushTouchDown(2, Vector2.One);
        input.NewFrame();

        AndroidTouchInput.Apply(input, action, 2, [new(1, 0, 0, 1f), new(2, 1, 1, 1f)]);

        Assert.Equal(TouchPhase.Stationary, input.Touches[0].Phase);
        Assert.Equal(TouchPhase.Ended, input.Touches[1].Phase);
    }

    /// <summary>A cancelled gesture cancels every finger in it.</summary>
    [Fact]
    public void CancelEndsEveryPointerAsCanceled()
    {
        var input = new BufferedInputSource();
        input.PushTouchDown(1, Vector2.Zero);
        input.PushTouchDown(2, Vector2.One);
        input.NewFrame();

        AndroidTouchInput.Apply(input, Cancel, 1, [new(1, 0, 0, 1f), new(2, 1, 1, 1f)]);

        Assert.All(input.Touches, static touch => Assert.Equal(TouchPhase.Canceled, touch.Phase));
    }

    /// <summary>Actions that are not touch contacts leave the state alone.</summary>
    [Fact]
    public void UnrelatedActionsAreIgnored()
    {
        var input = new BufferedInputSource();

        AndroidTouchInput.Apply(input, 7, 1, [new(1, 0, 0, 1f)]);

        Assert.Empty(input.Touches);
    }
}
