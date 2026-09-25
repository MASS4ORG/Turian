namespace Turian.Editor.Core;

/// <summary>
/// Asks the asset browser to show and select an asset. The inspector raises it when a reference
/// field is clicked; the browser panel — whatever
/// shell it belongs to — is what answers.
/// </summary>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class AssetRevealService
{
    /// <summary>Raised with the asset to show.</summary>
    public event Action<Guid>? Requested;

    /// <summary>Asks whichever browser is listening to reveal <paramref name="assetId"/>.</summary>
    /// <param name="assetId">The asset to show and select.</param>
    public void Reveal(Guid assetId)
    {
        if (assetId != Guid.Empty) Requested?.Invoke(assetId);
    }
}
