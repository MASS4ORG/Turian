using System.Numerics;
using Turian.Engine.Core;

namespace Turian.Android;

/// <summary>One pointer of an Android motion event, copied out because the platform recycles the event.</summary>
/// <param name="Id">The pointer's identifier for the whole contact.</param>
/// <param name="X">Horizontal position in surface pixels.</param>
/// <param name="Y">Vertical position in surface pixels.</param>
/// <param name="Pressure">The reported pressure.</param>
public readonly record struct AndroidPointer(int Id, float X, float Y, float Pressure);

/// <summary>Translates Android motion events into the engine's touch events.</summary>
public static class AndroidTouchInput
{
    const int ActionDown = 0;
    const int ActionUp = 1;
    const int ActionMove = 2;
    const int ActionCancel = 3;
    const int ActionPointerDown = 5;
    const int ActionPointerUp = 6;

    /// <summary>Applies one motion event to the input source.</summary>
    /// <param name="input">The source gameplay reads.</param>
    /// <param name="action">The masked action of the event.</param>
    /// <param name="actionPointer">The identifier of the pointer the action concerns.</param>
    /// <param name="pointers">Every pointer currently in the gesture.</param>
    public static void Apply(BufferedInputSource input, int action, int actionPointer,
        IReadOnlyList<AndroidPointer> pointers)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(pointers);
        switch (action)
        {
            case ActionDown or ActionPointerDown when Find(pointers, actionPointer) is { } pressed:
                input.PushTouchDown(pressed.Id, Position(pressed), pressed.Pressure);
                break;
            case ActionMove:
                foreach (var pointer in pointers)
                {
                    input.PushTouchMove(pointer.Id, Position(pointer), pointer.Pressure);
                }
                break;
            case ActionUp or ActionPointerUp:
                input.PushTouchUp(actionPointer);
                break;
            case ActionCancel:
                foreach (var pointer in pointers)
                {
                    input.PushTouchCancel(pointer.Id);
                }
                break;
        }
    }

    static AndroidPointer? Find(IReadOnlyList<AndroidPointer> pointers, int id)
    {
        foreach (var pointer in pointers)
        {
            if (pointer.Id == id)
            {
                return pointer;
            }
        }

        return null;
    }

    static Vector2 Position(AndroidPointer pointer) => new(pointer.X, pointer.Y);
}
