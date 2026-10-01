namespace Turian.Engine.Core;

/// <summary>
/// Injects a registered service into a scene object before its lifecycle callbacks.
/// Mark injected properties with <see cref="JsonIgnoreAttribute"/> so they are not persisted in scenes.
/// Injected services are never edited, so it also hides the property from forms.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
[MeansImplicitUse(ImplicitUseKindFlags.Assign)]
public sealed class InjectServiceAttribute : HideInEditorAttribute
{
    /// <summary>Allows an editor or headless host to leave this service unbound.</summary>
    public bool Optional { get; set; }
}
