namespace Turian.Engine.Core;

/// <summary>Generated component wiring invoked by the engine at activation and teardown.</summary>
public interface IDataAssetWiring
{
    /// <summary>Subscribes compiled typed handlers to designer-assigned event assets.</summary>
    void SubscribeDataAssetEvents();

    /// <summary>Disposes every generated subscription, including partially initialized wiring.</summary>
    void DisposeDataAssetEvents();
}
