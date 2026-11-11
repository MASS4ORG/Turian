namespace Turian.Engine.Core;

/// <summary>The environment in which a session's feature modules are composed.</summary>
public enum EngineSessionKind
{
    /// <summary>A device-free server or simulation.</summary>
    Headless,
    /// <summary>A standalone graphical client.</summary>
    Client,
    /// <summary>A Play Mode session using the editor's host.</summary>
    Editor,
}

/// <summary>Session capabilities available while modules register their own services.</summary>
public sealed record EngineSessionContext(EngineSessionKind Kind, bool Interactive = false);

/// <summary>A feature's session lifetime, independent of the rendering host.</summary>
public interface IEngineSessionParticipant
{
    /// <summary>Starts the feature after all services have been composed.</summary>
    void Start();
    /// <summary>Stops feature work before its services are disposed.</summary>
    void Stop();
}

/// <summary>A feature that stops device work while the application is in the background.</summary>
public interface IEngineSessionPauseParticipant
{
    /// <summary>Called on the game thread when the application leaves or returns to the foreground.</summary>
    /// <param name="paused">Whether the application is now in the background.</param>
    void SetPaused(bool paused);
}

/// <summary>Composes and tears down installed feature lifetimes in registration order.</summary>
public sealed class EngineSessionLifetime(IEnumerable<IEngineSessionParticipant> participants) : IDisposable
{
    readonly IEngineSessionParticipant[] participants = [.. participants];
    int started;

    /// <summary>Starts every feature; failed starts unwind features already started.</summary>
    public void Start()
    {
        if (started != 0) { return; }
        try
        {
            foreach (var participant in participants)
            {
                started++;
                participant.Start();
            }
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Tells every started feature that the application left or returned to the foreground.</summary>
    /// <param name="paused">Whether the application is now in the background.</param>
    public void SetPaused(bool paused)
    {
        foreach (var participant in participants.Take(started).OfType<IEngineSessionPauseParticipant>())
        {
            participant.SetPaused(paused);
        }
    }

    /// <summary>Stops every started feature in reverse order, including after a partial startup failure.</summary>
    public void Dispose()
    {
        List<Exception>? errors = null;
        while (started > 0)
        {
            try { participants[--started].Stop(); }
            catch (Exception ex) { (errors ??= []).Add(ex); }
        }
        if (errors is not null) { throw new AggregateException("Session features failed to stop.", errors); }
    }
}

/// <summary>Reads the session context without building an intermediate service provider.</summary>
public static class EngineSessionServices
{
    /// <summary>Returns the explicitly registered context, or a headless context for standalone module tests.</summary>
    public static EngineSessionContext GetEngineSession(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(EngineSessionContext))
            ?.ImplementationInstance as EngineSessionContext ?? new(EngineSessionKind.Headless);
    }
}
