namespace Gaya.Tests;

/// <summary>Checks that a tab with unsaved changes shows its dot in every built-in color theme.</summary>
[Collection(SerialTests.Name)]
public sealed class DirtyTabMarkerTests
{
    sealed class Tabs : IChromeItem
    {
        /// <inheritdoc />
        public void Render(Gui gui)
        {
            var active = "selected";
            gui.TabStrip(ref active, static tabs =>
            {
                tabs.Tab("Selected", closable: true, key: "selected", modified: true);
                tabs.Tab("Other", closable: true, key: "other", modified: true);
            }, id: "marked").Height(26);
        }
    }

    /// <summary>Both the selected tab's dot and another tab's are clearly lighter or darker than their tab.</summary>
    [Fact]
    public void UnsavedDotStandsOutOnEveryTab()
    {
        var image = Path.Combine(Path.GetTempPath(), $"gaya-dirty-tab-{Guid.NewGuid():N}.png");
        try
        {
            using var frame = new WorkbenchFrame();
            frame.Chrome.Register(new ChromeDescriptor("test.tabs", ChromeSlot.TopBar, static _ => new Tabs(), 26));
            var themes = frame.Themes.ColorThemes.Select(static theme => theme.Id)
                .Where(static id => id.StartsWith("gaya.", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(themes);

            var invisible = new List<string>();
            foreach (var theme in themes)
            {
                frame.Themes.ApplyColorTheme(theme);
                frame.Draw();
                frame.Draw();
                frame.Capture(image);

                var markers = WorkbenchFrame.Descendants(frame.Gui.RootNode!)
                    .Where(static node => node.Id.EndsWith("/close", StringComparison.Ordinal)
                                          && node.Id.Contains("marked", StringComparison.Ordinal))
                    .Select(static node => node.Rect).ToList();
                Assert.Equal(2, markers.Count);
                using var pixels = SKBitmap.Decode(image);
                foreach (var marker in markers)
                {
                    var dot = pixels.GetPixel((int)marker.Center.X, (int)marker.Center.Y);
                    var tab = pixels.GetPixel((int)marker.X + 1, (int)marker.Y + 1);
                    var contrast = Math.Abs(dot.Red - tab.Red) + Math.Abs(dot.Green - tab.Green)
                                                              + Math.Abs(dot.Blue - tab.Blue);
                    if (contrast < 90)
                    {
                        invisible.Add($"{theme}: dot {dot} on {tab}");
                    }
                }
            }

            Assert.Empty(invisible);
        }
        finally
        {
            File.Delete(image);
        }
    }
}
