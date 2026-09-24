namespace Turian.Engine.Core;

/// <summary>
/// Keys naming the platforms a texture bakes a separate artifact for. GPU texture compression is
/// not portable — desktop and console sample BC, Android ETC2 or ASTC, iOS ASTC — so one
/// <see cref="TextureAsset"/> owns a set of artifacts and the running platform picks one.
/// </summary>
/// <remarks>
/// Only <see cref="Pc"/> has an encoder today; it passes DDS block data through untouched. The
/// other keys are reserved so the cache and catalog already carry the shape a second encoder needs.
/// </remarks>
public static class TextureBuildTarget
{
    /// <summary>Desktop and console: BC1/BC3/BC5/BC7.</summary>
    public const string Pc = "pc";

    /// <summary>Android: ETC2 or ASTC. No encoder yet.</summary>
    public const string Android = "android";

    /// <summary>iOS: ASTC. No encoder yet.</summary>
    public const string Ios = "ios";

    /// <summary>The target the running process consumes.</summary>
    public static string Current => Pc;

    /// <summary>The targets an import actually bakes an artifact for.</summary>
    public static IReadOnlyList<string> Implemented { get; } = [Pc];
}
