namespace Turian;

/// <summary>
/// On a <c>partial</c> property of a <c>partial</c> DataAsset, generates an implementation that raises the asset's
/// <c>Changed</c> event whenever the value changes.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ObservableAttribute : Attribute;
