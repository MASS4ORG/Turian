using System.Diagnostics.CodeAnalysis;

namespace Turian.Engine.Core;

/// <summary>Host construction inputs available before feature services are composed.</summary>
public sealed record ClientHostContext(IAppSettings Settings, ILogger Logger, bool Development);

/// <summary>A rendering brick's window, input and presentation host for an already composed session.</summary>
public interface IClientRuntimeHost : IDisposable
{
    /// <summary>The host's input facade, available after Prepare.</summary>
    IInputSource Input { get; }
    /// <summary>The host's rendering facade, available after Prepare.</summary>
    IRenderDevice RenderDevice { get; }
    /// <summary>Creates host infrastructure without registering services or starting a session.</summary>
    void Prepare(ClientHostContext context);
    /// <summary>Initializes the window and rendering resources before startup scene loading.</summary>
    void Initialize(GameRuntime runtime, GamePlayerContext context);
    /// <summary>Drives the composed runtime until its window closes or the application quits.</summary>
    int Run(GameRuntime runtime);
}

/// <summary>Identifies an assembly providing a graphical runtime host with a public parameterless constructor.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
public sealed class ClientRuntimeHostAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type hostType) : Attribute
{
    /// <summary>The host implementation with a public parameterless constructor.</summary>
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type HostType { get; } = hostType;
}

/// <summary>Owns the graphical session lifecycle without depending on a rendering API.</summary>
public sealed class ClientPlayer(Func<IClientRuntimeHost>? createHost = null) : IGamePlayer
{
    /// <inheritdoc/>
    public int Run(GamePlayerContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Application.BeginSession(headless: false);
        using var interrupt = Application.QuitOnInterrupt();
        var host = createHost?.Invoke();
        GameRuntime? runtime = null;
        try
        {
            runtime = GameRuntime.StartClient(context, host);
            host ??= runtime.Services.GetRequiredService<IClientRuntimeHost>();
            host.Initialize(runtime, context);
            runtime.LoadStartupScene();
            return host.Run(runtime);
        }
        finally
        {
            try { runtime?.Dispose(); }
            finally { host?.Dispose(); }
        }
    }
}
