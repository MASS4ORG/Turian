using SkiaSharp;

namespace Gaya.Host;

public sealed partial class Workbench
{
    static readonly WindowIdentity DefaultIdentity = GayaIdentity();
    WindowIdentity? appliedIdentity;

    void SyncWindowIdentity(Gui gui)
    {
        if (!gui.Platform.TryGet<IWindowIdentityCapability>(out var window)) return;
        var identity = (app.Services.GetService(typeof(IWindowIdentity)) as IWindowIdentity)?.Current ?? DefaultIdentity;
        if (identity == appliedIdentity) return;
        window!.Title = identity.Title;
        ApplyWindowIcon(window, identity.Icon);
        appliedIdentity = identity;
    }

    static void ApplyWindowIcon(IWindowIdentityCapability window, ReadOnlyMemory<byte> encoded)
    {
        using var source = SKBitmap.Decode(encoded.ToArray());
        if (source is null) return;
        using var icon = new SKBitmap(new SKImageInfo(64, 64, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        using (var canvas = new SKCanvas(icon))
        using (var image = SKImage.FromBitmap(source))
            canvas.DrawImage(image, new SKRect(0, 0, 64, 64), new SKSamplingOptions(SKCubicResampler.Mitchell));
        window.SetIcon(icon.Width, icon.Height, icon.Bytes);
    }

    static WindowIdentity GayaIdentity()
    {
        using var stream = typeof(Workbench).Assembly.GetManifestResourceStream("Gaya.Host.Resources.logo.png")
            ?? throw new InvalidOperationException("The Gaya application icon is missing.");
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return new WindowIdentity("Gaya", bytes.ToArray());
    }
}
