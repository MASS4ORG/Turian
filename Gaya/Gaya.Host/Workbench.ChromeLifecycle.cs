namespace Gaya.Host;

/// <summary>Maintains frame snapshots and cached instances of the workbench's chrome contributions.</summary>
public sealed partial class Workbench
{
    Dictionary<string, int> chromeVersions = [];
    readonly Dictionary<ChromeSlot, IReadOnlyList<ChromeDescriptor>> chromeSlots = [];
    int chromeRevision = -1;

    /// <summary>Snapshots contributions before building so both GUI passes use the same instances and order.</summary>
    void SyncChrome()
    {
        if (chromeRevision == app.Chrome.Revision) return;

        var revision = app.Chrome.Revision;
        var current = app.Chrome.All.ToDictionary(item => item.Id, item => app.Chrome.VersionFor(item.Id));
        chromeSlots.Clear();
        foreach (var slot in Enum.GetValues<ChromeSlot>()) chromeSlots[slot] = app.Chrome.For(slot).ToArray();
        foreach (var (id, previous) in chromeVersions)
        {
            if (current.TryGetValue(id, out var next) && previous == next) continue;
            ReleaseChrome(id);
        }

        chromeVersions = current;
        chromeRevision = revision;
    }

    void ReleaseChrome(string id)
    {
        if (chromeInstances.Remove(id, out var item) && item is IDisposable disposable) disposable.Dispose();
    }

    IReadOnlyList<ChromeDescriptor> ChromeFor(ChromeSlot slot) => chromeSlots.GetValueOrDefault(slot) ?? [];

    void RenderOverlays(Gui gui)
    {
        foreach (var item in ChromeFor(ChromeSlot.Overlay))
            using (gui.Node(0, 0, $"chrome/Overlay/{item.Id}").AbsoluteScreen(0, 0).Enter())
                ResolveChrome(item).Render(gui);
    }
}
