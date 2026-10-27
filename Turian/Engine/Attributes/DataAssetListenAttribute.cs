namespace Turian;

/// <summary>Generates typed subscription ownership for a method on a partial component.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class DataAssetListenAttribute(string eventMember, int priority = 0) : Attribute
{
    /// <summary>The source event field or property, resolved by the generator at compile time.</summary>
    public string EventMember { get; } = eventMember;

    /// <summary>Higher priorities run first, with subscription order breaking ties.</summary>
    public int Priority { get; } = priority;
}
