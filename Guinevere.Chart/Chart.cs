using System.Globalization;
using System.Numerics;

namespace Guinevere.Chart;

/// <summary>A caller-projected timeline interval in milliseconds and a zero-based row.</summary>
/// <param name="Start">Interval start.</param>
/// <param name="End">Interval end.</param>
/// <param name="Row">Zero-based lane.</param>
/// <param name="Color">Interval fill color.</param>
/// <param name="Label">Optional text drawn when it fits in the bar.</param>
public readonly record struct TimelineSpan(double Start, double End, int Row, Color Color, string? Label);

/// <summary>Drawing and pointer hit testing for simple profiling charts.</summary>
public static class Chart
{
    /// <summary>Draws frame durations scaled to the supplied maximum, with a selection and budget guide.</summary>
    public static void DrawHistory(Gui gui, Rect area, ReadOnlySpan<float> frames, float maximum,
        float budget, int selected, Color barColor, Color selectionColor, Color budgetColor)
    {
        if (frames.IsEmpty || area.W <= 0 || area.H <= 0 || !float.IsFinite(maximum) || maximum <= 0)
        {
            return;
        }
        var slot = area.W / frames.Length;
        for (var i = 0; i < frames.Length; i++)
        {
            var value = float.IsFinite(frames[i]) ? Math.Clamp(frames[i], 0, maximum) : 0;
            var height = area.H * value / maximum;
            gui.DrawRect(new Rect(area.X + i * slot, area.Y + area.H - height, slot, height), barColor);
            if (i == selected)
            {
                gui.DrawRectBorder(new Rect(area.X + i * slot, area.Y, slot, area.H), selectionColor, 1);
            }
        }
        if (float.IsFinite(budget) && budget >= 0 && budget <= maximum)
        {
            var y = area.Y + area.H * (1 - budget / maximum);
            gui.DrawLine(new Vector2(area.X, y), new Vector2(area.X + area.W, y), budgetColor);
        }
    }

    /// <summary>Returns the frame slot under the pointer, or -1 outside the chart.</summary>
    public static int HitHistory(Rect area, int frameCount, Vector2 pointer)
    {
        if (frameCount <= 0 || !Contains(area, pointer))
        {
            return -1;
        }
        return Math.Min(frameCount - 1, (int)((pointer.X - area.X) / area.W * frameCount));
    }

    /// <summary>Draws a millisecond ruler and guides across a chart with an explicit visible range.</summary>
    public static void DrawTimeRuler(Gui gui, Rect area, double start, double end, float labelSize,
        Color labelColor, Color guideColor)
    {
        Span<double> ticks = stackalloc double[32];
        var count = TimeTicks(start, end, area.W, labelSize * 7, ticks);
        var right = area.X + area.W;
        for (var i = 0; i < count; i++)
        {
            var tick = ticks[i];
            var x = area.X + (float)((tick - start) / (end - start)) * area.W;
            gui.DrawLine(new Vector2(x, area.Y), new Vector2(x, area.Y + area.H), guideColor);
            var label = tick.ToString("G6", CultureInfo.InvariantCulture) + " ms";
            var layout = gui.LayoutText(label, size: labelSize);
            if (x + layout.Width + 4 <= right)
            {
                gui.DrawTextLayout(layout, new Vector2(x + 3, area.Y), labelColor);
            }
        }
    }

    /// <summary>Writes decimal ticks in the visible range, constrained by the pixel spacing and buffer.</summary>
    public static int TimeTicks(double start, double end, float width, float spacing, Span<double> destination)
    {
        var length = end - start;
        if (!double.IsFinite(length) || length <= 0 || !float.IsFinite(width) || width <= 0
            || !float.IsFinite(spacing) || spacing <= 0 || destination.IsEmpty)
        {
            return 0;
        }
        var intervals = Math.Clamp((double)width / spacing, 1, Math.Max(1, destination.Length - 1));
        var minimumStep = length / intervals;
        var power = Math.Pow(10, Math.Floor(Math.Log10(minimumStep)));
        if (power <= 0)
        {
            return 0;
        }
        var ratio = minimumStep / power;
        var step = power * (ratio switch { <= 1 => 1, <= 2 => 2, <= 5 => 5, _ => 10 });
        var first = Math.Ceiling(start / step) * step;
        if (first < start)
        {
            first += step;
        }
        var count = 0;
        for (var tick = first; double.IsFinite(tick) && tick >= start && tick <= end && count < destination.Length;)
        {
            destination[count++] = tick;
            var next = first + count * step;
            if (next <= tick)
            {
                break;
            }
            tick = next;
        }
        return count;
    }

    /// <summary>Draws visible intervals in caller-assigned rows within an explicit time range.</summary>
    public static void DrawTimeline(Gui gui, Rect area, ReadOnlySpan<TimelineSpan> spans, double start,
        double end, float rowHeight, Color labelColor, int selected = -1, Color selectionColor = default)
    {
        for (var i = 0; i < spans.Length; i++)
        {
            if (!TrySpanRect(area, spans[i], start, end, rowHeight, out var rect))
            {
                continue;
            }
            gui.DrawRect(rect, spans[i].Color);
            if (i == selected)
            {
                gui.DrawRectBorder(rect, selectionColor, 1);
            }
            if (!string.IsNullOrEmpty(spans[i].Label) && rect.W > 8 && rect.H > 4)
            {
                var layout = gui.LayoutText(spans[i].Label!, maxWidth: rect.W - 6, size: 11);
                if (layout.Height <= rect.H && layout.Width <= rect.W - 6)
                {
                    gui.DrawTextLayout(layout, new Vector2(rect.X + 3, rect.Y + (rect.H - layout.Height) / 2), labelColor);
                }
            }
        }
    }

    /// <summary>Returns the topmost interval under the pointer, or -1 when none is visible there.</summary>
    public static int HitTimeline(Rect area, ReadOnlySpan<TimelineSpan> spans, double start, double end,
        float rowHeight, Vector2 pointer)
    {
        if (!Contains(area, pointer))
        {
            return -1;
        }
        for (var i = spans.Length - 1; i >= 0; i--)
        {
            if (TrySpanRect(area, spans[i], start, end, rowHeight, out var rect) && Contains(rect, pointer))
            {
                return i;
            }
        }
        return -1;
    }

    static bool TrySpanRect(Rect area, TimelineSpan span, double start, double end, float rowHeight, out Rect rect)
    {
        rect = default;
        if (area.W <= 0 || area.H <= 0 || !double.IsFinite(start) || !double.IsFinite(end) || end <= start ||
            !double.IsFinite(span.Start) || !double.IsFinite(span.End) || span.End <= span.Start || span.Row < 0 ||
            !float.IsFinite(rowHeight) || rowHeight <= 0 || span.End <= start || span.Start >= end)
        {
            return false;
        }
        var top = area.Y + span.Row * rowHeight;
        if (top >= area.Y + area.H)
        {
            return false;
        }
        var left = area.X + (float)((Math.Max(start, span.Start) - start) / (end - start)) * area.W;
        var right = area.X + (float)((Math.Min(end, span.End) - start) / (end - start)) * area.W;
        right = Math.Min(area.X + area.W, Math.Max(left + 1, right));
        rect = new Rect(left, top, right - left, Math.Min(rowHeight, area.Y + area.H - top));
        return rect.W > 0 && rect.H > 0;
    }

    static bool Contains(Rect rect, Vector2 point) => rect.W > 0 && rect.H > 0 && point.X >= rect.X &&
        point.X < rect.X + rect.W && point.Y >= rect.Y && point.Y < rect.Y + rect.H;
}
