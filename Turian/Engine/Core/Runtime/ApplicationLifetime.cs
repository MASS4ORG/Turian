namespace Turian.Engine.Core;

/// <summary>The environment in which an application's feature modules are composed.</summary>
public enum ApplicationKind
{
    /// <summary>A device-free server or simulation.</summary>
    Headless,
    /// <summary>A standalone graphical client.</summary>
    Client,
    /// <summary>A Play Mode run using the editor's host.</summary>
    Editor,
}

/// <summary>Application capabilities available while modules register their own services.</summary>
public sealed record ApplicationContext(ApplicationKind Kind, bool Interactive = false);

/// <summary>A feature's application lifetime, independent of the rendering host.</summary>
public interface IApplicationParticipant
{
    /// <summary>Starts the feature after all services have been composed.</summary>
    void Start();
    /// <summary>Stops feature work before its services are disposed.</summary>
    void Stop();
}

/// <summary>A feature that stops device work while the application is in the background.</summary>
public interface IApplicationPauseParticipant
{
    /// <summary>Called on the game thread when the application leaves or returns to the foreground.</summary>
    /// <param name="paused">Whether the application is now in the background.</param>
    void SetPaused(bool paused);
}

/// <summary>Composes and tears down installed feature lifetimes in registration order.</summary>
public sealed class ApplicationLifetime(IEnumerable<IApplicationParticipant> participants) : IDisposable
{
    readonly IApplicationParticipant[] participants = [.. participants];
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
        foreach (var participant in participants.Take(started).OfType<IApplicationPauseParticipant>())
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
        if (errors is not null) { throw new AggregateException("Application features failed to stop.", errors); }
    }
}

/// <summary>Reads the application context without building an intermediate service provider.</summary>
public static class ApplicationServices
{
    /// <summary>Returns the explicitly registered context, or a headless context for standalone module tests.</summary>
    public static ApplicationContext GetApplicationContext(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.LastOrDefault(static descriptor => descriptor.ServiceType == typeof(ApplicationContext))
            ?.ImplementationInstance as ApplicationContext ?? new(ApplicationKind.Headless);
    }
}
