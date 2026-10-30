namespace Turian;

/// <summary>Generates a typed DataAssetEvent definition for a record payload.</summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class)]
public sealed class DataAssetEventDefinitionAttribute(string name, string typeId) : Attribute
{
    /// <summary>The generated event class name in the payload's namespace.</summary>
    public string Name { get; } = name;

    /// <summary>The stable serialized type GUID, independent of class and namespace names.</summary>
    public string EventTypeId { get; } = typeId;
}
