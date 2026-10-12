using Silk.NET.Input;

namespace Turian.Tests;

/// <summary>Tests that the input manager tolerates a key release with no matching press.</summary>
public sealed class InputManagerTests
{
    /// <summary>A KeyUp for a key never pressed is ignored instead of throwing.</summary>
    [Fact]
    public void KeyUpWithoutKeyDownIsIgnored()
    {
        var input = (InputManager)RuntimeHelpers.GetUninitializedObject(typeof(InputManager));
        var keysDown = new List<Key>();
        typeof(InputManager).GetField("keysDown", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(input, keysDown);
        var handler = typeof(InputManager).GetMethod("KeyboardOnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var exception = Record.Exception(() => handler.Invoke(input, [Substitute.For<IKeyboard>(), Key.A, 0]));

        Assert.Null(exception);
        Assert.DoesNotContain(Key.A, keysDown);
    }
}
