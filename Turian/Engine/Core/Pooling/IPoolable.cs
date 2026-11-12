namespace Turian.Engine.Core;

/// <summary>
/// Lets a pooled object or a component of a pooled node reset its state each time it is reused.
/// </summary>
public interface IPoolable
{
    /// <summary>Called when the object leaves its pool, before it is used again.</summary>
    void OnGetFromPool();

    /// <summary>Called when the object returns to its pool, before it is stored.</summary>
    void OnReleaseToPool();
}
