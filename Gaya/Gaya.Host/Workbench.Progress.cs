using System.Globalization;

namespace Gaya.Host;

public sealed partial class Workbench
{
    UiBlockerProgress? frameProgress;

    void RenderBlocker(Gui gui)
    {
        if (gui.Pass == Pass.Pass1Build)
            frameProgress = app.Services.GetService(typeof(IUiBlocker)) is IUiBlocker { IsBlocked: true } blocker
                ? blocker.Progress ?? new UiBlockerProgress(blocker.Message)
                : null;
        if (frameProgress is not { } progress) return;
        gui.RequestFrame();

        using (gui.Node(gui.ScreenRect.W, gui.ScreenRect.H, "__uiBlocker")
                   .AbsoluteScreen(0, 0).Direction(Axis.Vertical).ContentAlignX(0.5f).ContentAlignY(0.5f)
                   .BlockInput().Enter())
        {
            gui.SetZIndex(10_000);
            gui.DrawBackgroundRect(Theme.Scrim);
            ProgressCard(gui, progress);
        }
    }

    void ProgressCard(Gui gui, UiBlockerProgress progress)
    {
        var t = Theme;
        var width = Math.Max(1, Math.Min(t.Scale(520), gui.ScreenRect.W - t.Scale(32)));
        using (gui.Node(width, -1, "__uiBlocker/card").Direction(Axis.Vertical)
                   .Padding(t.Scale(24)).Gap(t.Scale(14)).Enter())
        {
            gui.DrawBackgroundRect(t.Panel, t.Scale(8));
            gui.DrawRectBorder(gui.CurrentNode.Rect, t.Border, 1, t.Scale(8));
            gui.DrawText(progress.Title, t.Text(18), t.Ink, centerInRect: false);
            if (progress.Detail.Length > 0)
                gui.DrawText(progress.Detail, t.Text(12), t.InkDim, centerInRect: false);
            using (gui.Node(-1, t.Scale(6), "__uiBlocker/progress").ExpandWidth().Enter())
                gui.ProgressBar(progress.Fraction, -1, t.Scale(6));
            ProgressSummary(gui, progress);
        }
    }

    void ProgressSummary(Gui gui, UiBlockerProgress progress)
    {
        var t = Theme;
        using (gui.Node(-1, -1, "__uiBlocker/summary").ExpandWidth().Direction(Axis.Horizontal).Enter())
        {
            gui.DrawText($"{progress.Elapsed.TotalSeconds:0}s", t.Text(11), t.InkDim, centerInRect: false);
            gui.Node().ExpandWidth();
            var counter = progress.Counter;
            if (progress.Fraction is { } fraction)
                counter = $"{counter}  {fraction.ToString("P0", CultureInfo.InvariantCulture)}".Trim();
            if (counter.Length > 0) gui.DrawText(counter, t.Text(11), t.InkDim, centerInRect: false);
        }
    }
}
