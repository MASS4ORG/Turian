namespace Turian.Engine.Core;

/// <summary>Static composition supplied by a game's generated launcher before runtime startup.</summary>
public static class ExportRuntime
{
    /// <summary>The generated module registrations, or null in the dynamically composed editor.</summary>
    public static Action<IServiceCollection>? ConfigureServices { get; set; }

    /// <summary>The statically selected player, or null when the editor discovers players.</summary>
    public static Func<IGamePlayer>? CreatePlayer { get; set; }
}
