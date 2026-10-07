namespace Turian.Tests;

/// <summary>Verifies application identity changes through a headless native-window capability.</summary>
[Collection(SerialTests.Name)]
public sealed class WindowIdentityTests
{
    sealed class Window : IWindowIdentityCapability
    {
        /// <inheritdoc />
        public string Title { get; set; } = "";
        /// <summary>Number of icon updates requested by the host.</summary>
        public int IconUpdates { get; private set; }
        /// <summary>Last supplied RGBA icon.</summary>
        public byte[] Pixels { get; private set; } = [];

        /// <inheritdoc />
        public void SetIcon(int width, int height, ReadOnlySpan<byte> pixels)
        {
            Assert.Equal(64, width);
            Assert.Equal(64, height);
            Pixels = pixels.ToArray();
            IconUpdates++;
        }
    }

    /// <summary>Gaya is the default identity; an open project switches title and icon once and follows name edits.</summary>
    [Fact]
    public void WindowIdentityFollowsProjectNameAndUsesApplicationIcons()
    {
        var settings = new SettingsService();
        var identity = new ProjectWindowIdentity(settings);
        Assert.Null(identity.Current);
        using var frame = new GayaChromeTests.Frame(identity: identity);
        var window = new Window();
        frame.Gui.Platform.Register<IWindowIdentityCapability>(window);
        frame.Draw();
        Assert.Equal("Gaya", window.Title);
        Assert.Equal(64 * 64 * 4, window.Pixels.Length);
        Assert.Contains(window.Pixels, channel => channel != 0);
        var gaya = window.Pixels;
        var project = new AppSettings { ProjectAbsoluteDir = "/projects/my-game", Title = "my-game" };
        project.Get<PlayerSettings>().ProductName = "My Game";
        settings.Set(project);
        frame.Draw();
        Assert.Equal("My Game - Turian", window.Title);
        Assert.False(gaya.SequenceEqual(window.Pixels));
        Assert.Equal(2, window.IconUpdates);
        frame.Draw();
        Assert.Equal(2, window.IconUpdates);
        project.Get<PlayerSettings>().ProductName = "Renamed Game";
        frame.Draw();
        Assert.Equal("Renamed Game - Turian", window.Title);
        Assert.Equal(3, window.IconUpdates);
    }
}
