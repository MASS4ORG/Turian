using Color = Turian.Engine.Core.Color;

namespace Turian.Tests;

/// <summary>
/// Tests for <see cref="Color"/> and <see cref="Color32"/>: the <see cref="Color32"/> JSON
/// round-trip (it silently lost data before <see cref="Color32JsonConverter"/> existed) and the
/// <see cref="Color"/>/<see cref="Vector4"/> memory-layout interop the batch paths rely on.
/// </summary>
public class ColorTests
{
    static JsonSerializerOptions Options()
    {
        var options = new JsonSerializerOptions { IncludeFields = true };
        options.Converters.Add(new Color32JsonConverter());
        return options;
    }

    /// <summary>A non-trivial packed color survives a JSON round-trip via the hex converter.</summary>
    [Fact]
    public void Color32_RoundTripsThroughJson()
    {
        var options = Options();
        var color = new Color32(0x4C, 0x8A, 0x2F, 0xE1);

        var json = JsonSerializer.Serialize(color, options);
        var back = JsonSerializer.Deserialize<Color32>(json, options);

        Assert.Equal("\"#4C8A2FE1\"", json);
        Assert.Equal(color.Rgba, back.Rgba);
        Assert.Equal(color.R, back.R);
        Assert.Equal(color.G, back.G);
        Assert.Equal(color.B, back.B);
        Assert.Equal(color.A, back.A);
    }

    /// <summary><see cref="Color"/> is exactly 16 bytes: bit-compatible with <see cref="Vector4"/>.</summary>
    [Fact]
    public void Color_IsSixteenBytes()
    {
        Assert.Equal(16, Unsafe.SizeOf<Color>());
    }

    /// <summary>A batch reinterpret-cast between <see cref="Color"/> and <see cref="Vector4"/> round-trips.</summary>
    [Fact]
    public void Color_ReinterpretsAsVector4()
    {
        var color = new Color(0.2f, 0.4f, 0.6f, 0.8f);

        ref var asVector4 = ref Unsafe.As<Color, Vector4>(ref color);

        Assert.Equal(color.R, asVector4.X);
        Assert.Equal(color.G, asVector4.Y);
        Assert.Equal(color.B, asVector4.Z);
        Assert.Equal(color.A, asVector4.W);

        ref var roundTripped = ref Unsafe.As<Vector4, Color>(ref asVector4);
        Assert.Equal(color, roundTripped);
    }

    /// <summary>The explicit <see cref="Vector4"/> conversion operators agree with the reinterpret path.</summary>
    [Fact]
    public void Color_ConvertsToAndFromVector4()
    {
        var color = new Color(0.1f, 0.2f, 0.3f, 0.4f);

        var vector = (Vector4)color;
        var back = (Color)vector;

        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0.4f), vector);
        Assert.Equal(color, back);
    }
}
