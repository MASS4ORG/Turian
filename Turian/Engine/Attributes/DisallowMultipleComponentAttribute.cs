namespace Turian;

/// <summary>
/// Prevents more than one instance of this component type from being added to a Node.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class DisallowMultipleComponentAttribute : Attribute { }
