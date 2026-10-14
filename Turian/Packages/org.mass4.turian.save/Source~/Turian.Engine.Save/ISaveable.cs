namespace Turian.Engine.Save;

/// <summary>Opt-in capture and restore for entities or components using game-owned stable identifiers.</summary>
public interface ISaveable<T>
{
    /// <summary>Persistent identity, assigned by the game and preserved across sessions.</summary>
    Guid SaveId { get; }
    /// <summary>Captures owned state on the simulation thread without retaining live mutable objects.</summary>
    T CaptureSave();
    /// <summary>Applies validated state on the simulation thread after entities and references are resolved.</summary>
    void RestoreSave(T state);
}
