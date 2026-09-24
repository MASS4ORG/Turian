namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="UiDocumentComponent"/>: its settings round-trip through polymorphic JSON
/// and the code-only builder is not serialized.
/// </summary>
public sealed class UiDocumentComponentTests
{
    static JsonSerializerOptions Options()
    {
        TypeRegistry.ScanAssembly(typeof(UiDocumentComponent).Assembly);
        var options = new JsonSerializerOptions { IncludeFields = true };
        options.Converters.Add(new ObjectJsonSerializer<UiDocumentComponent>());
        return options;
    }

    /// <summary>Placement and scaling settings survive a serialization round-trip.</summary>
    [Fact]
    public void Settings_RoundTripThroughJson()
    {
        var options = Options();
        var component = new UiDocumentComponent
        {
            Mode = UiRenderMode.WorldSpace,
            SortOrder = 7,
            ScaleMode = UiScaleMode.ScaleWithScreenSize,
            ReferenceResolution = new Int2(1280, 720),
            PanelSize = new Int2(800, 600),
            OnBuild = _ => { },
        };

        var json = JsonSerializer.Serialize(component, options);
        var back = JsonSerializer.Deserialize<UiDocumentComponent>(json, options)!;

        Assert.Equal(UiRenderMode.WorldSpace, back.Mode);
        Assert.Equal(7, back.SortOrder);
        Assert.Equal(UiScaleMode.ScaleWithScreenSize, back.ScaleMode);
        Assert.Equal(new Int2(1280, 720), back.ReferenceResolution);
        Assert.Equal(new Int2(800, 600), back.PanelSize);
        Assert.Null(back.OnBuild);
    }

    /// <summary>The JSON carries a stable type id and omits the builder delegate.</summary>
    [Fact]
    public void Json_HasTypeIdAndNoBuilder()
    {
        var options = Options();
        var json = JsonSerializer.Serialize(new UiDocumentComponent { OnBuild = _ => { } }, options);

        Assert.Contains("__TypeId", json, StringComparison.Ordinal);
        Assert.DoesNotContain("OnBuild", json, StringComparison.Ordinal);
    }
}
