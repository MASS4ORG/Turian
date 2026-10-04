namespace Turian.Tests;

/// <summary>Checks live editor frame pacing without sleeping or opening a desktop window.</summary>
[Collection(SerialTests.Name)]
public sealed class FrameRateTests
{
    /// <summary>The performance page restores its cap and shares the same live object across registrations.</summary>
    [Fact]
    public void PreferencesRestoreAndRegisterOnce()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            Assert.Throws<ArgumentNullException>(() => FrameRateSettings.Register(null!));
            var settings = new EditorSettings(NullLogger.Instance, path);
            var performance = FrameRateSettings.Register(settings);
            Assert.Equal(60, performance.CapFps);
            Assert.Same(performance, FrameRateSettings.Register(settings));
            performance.CapFps = 0;
            settings.Save();
            Assert.Equal(0, FrameRateSettings.Register(new EditorSettings(NullLogger.Instance, path)).CapFps);
            performance.CapFps = -5;
            Assert.Equal(0, performance.CapFps);
            using var app = PluginHost.Load([], NullLogger.Instance);
            using var workbench = new Workbench(app);
            Assert.NotNull(workbench.FrameRate);
            Assert.Contains(app.Settings.Pages, page => page.Id == FrameRateSettings.PageId);
            var limiter = new FrameRateLimiter(performance);
            ((Action<TimeSpan>)typeof(FrameRateLimiter).GetField("delay", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(limiter)!)(TimeSpan.Zero);
            Assert.Throws<ArgumentNullException>(() => new FrameRateLimiter(null!));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Pacing subtracts render time, applies cap changes and resets scheduling while uncapped.</summary>
    [Fact]
    public void PacingHonorsRenderTimeAndLiveCap()
    {
        var preferences = new FrameRateSettings();
        long now = 0;
        var waits = new List<TimeSpan>();
        Action<TimeSpan> delay = duration =>
        {
            waits.Add(duration);
            now += (long)Math.Round(duration.TotalSeconds * 60000);
        };
        var limiter = (FrameRateLimiter)Activator.CreateInstance(typeof(FrameRateLimiter),
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            [preferences, (Func<long>)(() => now), 60000L, delay], null)!;
        limiter.Wait();
        Assert.Empty(waits);
        now += 300;
        limiter.Wait();
        Assert.Equal(700d / 60000, waits.Single().TotalSeconds, 6);
        now += 3000;
        limiter.Wait();
        Assert.Single(waits);
        preferences.CapFps = 30;
        limiter.Wait();
        Assert.Equal(2000d / 60000, waits[^1].TotalSeconds, 6);
        preferences.CapFps = 0;
        limiter.Wait();
        limiter.Wait();
        Assert.Equal(2, waits.Count);
        preferences.CapFps = 60;
        limiter.Wait();
        Assert.Equal(2, waits.Count);
        limiter.Wait();
        Assert.Equal(1000d / 60000, waits[^1].TotalSeconds, 6);
    }
}
