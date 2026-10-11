namespace Turian.Editor.Core;

/// <summary>What a built game runs as.</summary>
public enum BuildTarget
{
    /// <summary>A player with a window, renderer and input: the game people play.</summary>
    Client,

    /// <summary>
    /// A dedicated server: client-only bricks and the bricks depending on them are left out, scripts compile with
    /// <c>TURIAN_SERVER</c>, and the game runs the engine's headless player.
    /// </summary>
    Server,
}

/// <summary>
/// One way the project builds a game, the Unity build profile counterpart: the target, platform, configuration and
/// script symbols. A project keeps several, such as a client and a server built from the same scenes and scripts.
/// </summary>
[CreateAssetMenu(fileName: "NewBuildProfile", path: "Build/Build Profile")]
[TypeId(TypeIdValue)]
public class BuildProfile : DataAsset
{
    /// <summary>The <see cref="TypeIdAttribute"/> value, read by build tools without loading the asset.</summary>
    public const string TypeIdValue = "871cd819-cb92-4f78-a849-4a66309b3de6";

    /// <summary>The symbol server builds define for their scripts, as Unity's <c>UNITY_SERVER</c>.</summary>
    public const string ServerSymbol = "TURIAN_SERVER";

    /// <summary>The profile name; empty uses the file name. Exports go to <c>Output/&lt;name&gt;</c>.</summary>
    public string? Name { get; set; }

    /// <summary>The id of the <see cref="IBuildPlatform"/> the game exports to; <c>desktop</c> by default.</summary>
    public string Platform { get; set; } = BuildPlatforms.Desktop;

    /// <summary>What the game runs as.</summary>
    public BuildTarget Target { get; set; } = BuildTarget.Client;

    /// <summary>The .NET runtime identifier to publish for, such as <c>linux-x64</c>; empty uses this machine's.</summary>
    public string? RuntimeIdentifier { get; set; }

    /// <summary>Release ships optimized code without debug symbols; Debug keeps them and shows diagnostics.</summary>
    public BuildConfiguration Configuration { get; set; } = BuildConfiguration.Release;

    /// <summary>Extra conditional compilation symbols for the game's scripts, separated by semicolons.</summary>
    public string? ScriptingDefines { get; set; }

    /// <summary>The symbols the game's scripts compile with: the target's own, then <see cref="ScriptingDefines"/>.</summary>
    public IReadOnlyList<string> Symbols =>
    [
        .. Target == BuildTarget.Server ? [ServerSymbol] : Array.Empty<string>(),
        .. (ScriptingDefines ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    ];
}
