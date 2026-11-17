namespace Turian.Testing;

/// <summary>Marks a synchronous component test, with no parameters or a single TestScene parameter.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class SceneTestAttribute : Attribute;

/// <summary>Reports a failed expectation in user tests.</summary>
public sealed class SceneAssertionException(string message) : Exception(message);

/// <summary>Framework-independent expectations for component tests.</summary>
public static class SceneAssert
{
    /// <summary>Fails when a condition is false.</summary>
    public static void True(bool condition, string message = "Expected the condition to be true.")
    {
        if (!condition) { throw new SceneAssertionException(message); }
    }

    /// <summary>Compares values using their default equality comparer.</summary>
    public static void Equal<T>(T expected, T actual) =>
        True(EqualityComparer<T>.Default.Equals(expected, actual), $"Expected {expected}; received {actual}.");

    /// <summary>Returns the value or fails when it is null.</summary>
    public static T NotNull<T>(T? value) where T : class =>
        value ?? throw new SceneAssertionException("Expected a non-null value.");
}
