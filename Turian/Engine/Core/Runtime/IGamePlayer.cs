namespace Turian.Engine.Core;

/// <summary>What a game's launcher hands its player.</summary>
/// <param name="ProjectDirectory">The folder the game runs from, holding its settings index and content.</param>
/// <param name="Logger">The game's logger.</param>
/// <param name="BuildServices">Builds the scene service provider; the launcher owns the container implementation.</param>
/// <param name="Development">Whether this is a development build, which shows diagnostics such as frame rate.</param>
/// <param name="Content">Where the game's packaged content is read from, such as APK assets; <c>null</c> reads
/// <paramref name="ProjectDirectory"/> from disk.</param>
public sealed record GamePlayerContext(
    string ProjectDirectory,
    ILogger Logger,
    Func<IServiceCollection, IServiceProvider> BuildServices,
    bool Development = false,
    IContentDirectory? Content = null)
{
    /// <summary>The game's packaged content: <see cref="Content"/>, else the project folder on disk.</summary>
    public IContentDirectory ContentDirectory => Content ?? new FileContentDirectory(ProjectDirectory);
}

/// <summary>
/// Runs a session until it quits. Rendering bricks declare <see cref="ClientRuntimeHostAttribute"/>;
/// Core's <see cref="ClientPlayer"/> owns composition and the session lifecycle.
/// </summary>
public interface IGamePlayer
{
    /// <summary>Runs the game in <paramref name="context"/> and returns the process exit code.</summary>
    /// <param name="context">The launcher's project folder, logger and service container.</param>
    /// <returns>The exit code.</returns>
    int Run(GamePlayerContext context);
}

/// <summary>Identifies a custom entry point provided by a loaded assembly.</summary>
/// <param name="playerType">A type with a public parameterless constructor implementing the player.</param>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class GamePlayerAttribute(Type playerType) : Attribute
{
    /// <summary>The player type.</summary>
    public Type PlayerType { get; } = playerType;
}

/// <summary>Finds the player a game's loaded bricks provide.</summary>
public static class GamePlayers
{
    /// <summary>Returns the player of the first assembly declaring one, or <c>null</c> when none is loaded.</summary>
    /// <param name="assemblies">The assemblies to look in.</param>
    /// <returns>The player, or <c>null</c>.</returns>
    public static IGamePlayer? Find(IEnumerable<Assembly> assemblies)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        foreach (var assembly in assemblies)
        {
            if (!assembly.IsDynamic && assembly.GetCustomAttribute<GamePlayerAttribute>() is { } attribute)
            {
                return (IGamePlayer)Activator.CreateInstance(attribute.PlayerType)!;
            }
            if (!assembly.IsDynamic && assembly.GetCustomAttribute<ClientRuntimeHostAttribute>() is { } host)
            {
                return new ClientPlayer(() => (IClientRuntimeHost)Activator.CreateInstance(host.HostType)!);
            }
        }

        return null;
    }

    /// <summary>The loaded brick's player, or the <see cref="HeadlessPlayer"/> when no brick provides one.</summary>
    /// <returns>The player to run the game with.</returns>
    public static IGamePlayer FindOrHeadless()
    {
        return Find(AppDomain.CurrentDomain.GetAssemblies()) ?? new HeadlessPlayer();
    }
}
