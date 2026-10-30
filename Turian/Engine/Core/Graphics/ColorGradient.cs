namespace Turian.Engine.Core;

/// <summary>A linear-light RGBA sample at a normalized gradient position.</summary>
/// <param name="Time">Normalized position in the range zero to one.</param>
/// <param name="Color">Linear RGBA channels at this position.</param>
public readonly record struct GradientKey(float Time, Color Color);

/// <summary>An immutable, piecewise-linear RGBA gradient with clamped endpoints.</summary>
public sealed class ColorGradient
{
    /// <summary>Gets or replaces distinct, ordered keys within zero to one.</summary>
    public IReadOnlyList<GradientKey> Keys
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            CurveValidation.Times(value.Select(static key => key.Time));
            foreach (var key in value)
            {
                CurveValidation.Finite(key.Color.R);
                CurveValidation.Finite(key.Color.G);
                CurveValidation.Finite(key.Color.B);
                CurveValidation.Finite(key.Color.A);
            }
            field = Array.AsReadOnly(value.ToArray());
        }
    } = Array.AsReadOnly([new GradientKey(0, Color.White), new GradientKey(1, Color.White)]);

    /// <summary>Evaluates a finite normalized position, interpolating RGB and alpha independently.</summary>
    public Color Evaluate(float time)
    {
        var index = CurveValidation.Segment(Keys.Select(static key => key.Time), time);
        if (index == 0)
        {
            return Keys[0].Color;
        }

        var a = Keys[index - 1];
        var b = Keys[index];
        return Color.Lerp(a.Color, b.Color, Math.Clamp((time - a.Time) / (b.Time - a.Time), 0, 1));
    }
}
