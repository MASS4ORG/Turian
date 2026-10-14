namespace Turian.Tests;

/// <summary>A camera's background color: its default and how scenes store it.</summary>
public class CameraComponentTests
{
    /// <summary>A new camera clears with the light blue the Scene view uses by default.</summary>
    [Fact]
    public void BackgroundColorDefaultsToTheSceneViewSky()
    {
        Assert.Equal(new Vector4(new SceneViewSettings().EmptySkyColor, 1f), (Vector4)new CameraComponent().BackgroundColor);
    }

    /// <summary>A scene keeps an authored background, and a scene saved before the property loads the default.</summary>
    [Fact]
    public void BackgroundColorRoundTripsAndDefaultsInOlderScenes()
    {
        var node = new Node();
        node.AddComponent(new CameraComponent { BackgroundColor = new Color(1f, 0.5f, 0.25f, 0.75f) });

        var json = Serializer.Serialize(node);
        var loaded = Serializer.LoadData<Node>(json)!.GetComponent<CameraComponent>()!;
        Assert.Equal(new Color(1f, 0.5f, 0.25f, 0.75f), loaded.BackgroundColor);

        var older = Regex.Replace(json, "\"BackgroundColor\":\\s*\\{[^}]*\\},?", "");
        Assert.DoesNotContain("BackgroundColor", older);
        Assert.Equal(new CameraComponent().BackgroundColor,
            Serializer.LoadData<Node>(older)!.GetComponent<CameraComponent>()!.BackgroundColor);
    }
}
