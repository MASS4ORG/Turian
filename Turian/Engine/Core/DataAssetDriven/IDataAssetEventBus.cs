namespace Turian.Engine.Core;

/// <summary>A reflection-free gameplay broker; implementations own subscriptions and typed worker queues.</summary>
interface IDataAssetEventBus : IDisposable
{
    DataAssetEventChannel<T> Prepare<T>(Guid topic);
    void Drain();
    IReadOnlyList<DataAssetSubscription> Listeners(Guid topic);
}

/// <summary>A listener's asset identity, component identity, priority and stable subscription sequence.</summary>
public sealed record DataAssetSubscription(Guid Topic, Guid Owner, int Priority, long Sequence);

/// <summary>A gameplay raise, recorded once per topic rather than once per listener.</summary>
public readonly record struct DataAssetEventTrace(long Sequence, Guid Topic, long Timestamp, int ListenerCount);

/// <summary>Receives allocation-free dispatch diagnostics on the owning game-loop thread.</summary>
public interface IDataAssetEventObserver
{
    /// <summary>Observes a raise immediately before delivery; payloads are never boxed for tracing.</summary>
    void OnRaised(in DataAssetEventTrace trace);
}
