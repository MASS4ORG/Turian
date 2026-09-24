namespace Turian;

/// <summary>
/// Declares required companion components for a component type.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = true)]
public sealed class RequireComponentAttribute : Attribute
{
    /// <summary>
    /// Initializes a new instance of the <see cref="RequireComponentAttribute"/> class.
    /// </summary>
    /// <param name="componentType">The required component type.</param>
    public RequireComponentAttribute(Type componentType)
    {
        ComponentType = componentType ?? throw new ArgumentNullException(nameof(componentType));
    }

    /// <summary>
    /// Gets the required component type.
    /// </summary>
    public Type ComponentType { get; }
}
