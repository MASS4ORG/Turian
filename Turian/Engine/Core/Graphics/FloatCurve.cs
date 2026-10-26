namespace Turian.Engine.Core;

/// <summary>A width sample at a normalized position along a line or trail lifetime.</summary>
/// <param name="Time">Normalized position in the range zero to one.</param>
/// <param name="Value">Nonnegative width at this position.</param>
public readonly record struct CurveKey(float Time, float Value);

/// <summary>An immutable, piecewise-linear curve with clamped endpoints.</summary>
public sealed class FloatCurve
{
    /// <summary>Gets or replaces the ordered keys; times must be distinct and within zero to one.</summary>
    public IReadOnlyList<CurveKey> Keys
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            CurveValidation.Times(value.Select(static key => key.Time));
            foreach (var key in value)
            {
                CurveValidation.Finite(key.Value);
                ArgumentOutOfRangeException.ThrowIfNegative(key.Value);
            }
            field = Array.AsReadOnly(value.ToArray());
        }
    } = Array.AsReadOnly([new CurveKey(0, 1), new CurveKey(1, 1)]);

    /// <summary>Evaluates a finite normalized position, clamping beyond the end keys.</summary>
    public float Evaluate(float time)
    {
        var index = CurveValidation.Segment(Keys.Select(static key => key.Time), time);
        if (index == 0)
        {
            return Keys[0].Value;
        }

        var a = Keys[index - 1];
        var b = Keys[index];
        return float.Lerp(a.Value, b.Value, Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1));
    }
}

static class CurveValidation
{
    internal static void Finite(float value)
    {
        if (!float.IsFinite(value))
        {
            throw new ArgumentException("Samples must be finite.", nameof(value));
        }
    }

    internal static void Times(IEnumerable<float> times)
    {
        var previous = -1f;
        foreach (var time in times)
        {
            Finite(time);
            if (time < 0 || time > 1 || time <= previous)
            {
                throw new ArgumentException("Keys must have increasing times in the range zero to one.", nameof(times));
            }

            previous = time;
        }
        if (previous < 0)
        {
            throw new ArgumentException("At least one key is required.", nameof(times));
        }
    }

    internal static int Segment(IEnumerable<float> times, float time)
    {
        Finite(time);
        var index = 0;
        foreach (var key in times)
        {
            if (time <= key)
            {
                return index;
            }

            index++;
        }
        return index - 1;
    }
}
