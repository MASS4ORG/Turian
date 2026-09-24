namespace Turian.Tests;

/// <summary>Verifies which component types the Inspector's Add Component menu offers.</summary>
public class ComponentRegistryTests
{
    /// <summary>A component the way a project declares one: no menu attribute.</summary>
    public sealed class PlainProjectComponent : Component;

    /// <summary>An abstract base, which cannot be instantiated.</summary>
    public abstract class AbstractProjectComponent : Component;

    /// <summary>A project's component is offered without <see cref="ComponentContextMenuAttribute"/>.</summary>
    [Fact]
    public void ProjectComponentWithoutAttributeIsOffered() =>
        Assert.True(ComponentRegistry.IsInspectable(typeof(PlainProjectComponent)));

    /// <summary>An engine component with the attribute is offered.</summary>
    [Fact]
    public void EngineComponentWithAttributeIsOffered() =>
        Assert.True(ComponentRegistry.IsInspectable(typeof(LightComponent)));

    /// <summary>The engine's internal placeholder is never offered.</summary>
    [Fact]
    public void EngineComponentWithoutAttributeIsNotOffered() =>
        Assert.False(ComponentRegistry.IsInspectable(typeof(MissingComponent)));

    /// <summary>Abstract types are never offered.</summary>
    [Fact]
    public void AbstractComponentIsNotOffered() =>
        Assert.False(ComponentRegistry.IsInspectable(typeof(AbstractProjectComponent)));
}
