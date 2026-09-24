using System.ComponentModel;

namespace Turian.Editor.Core;

/// <summary>
/// Wraps an asset and exposes UI-friendly metadata for tabs.
/// </summary>
public sealed class AssetWrapper : IEquatable<AssetWrapper>, INotifyPropertyChanged, IDisposable
{
    /// <summary>
    /// Gets the wrapped asset object.
    /// </summary>
    public Asset? Asset { get; }

    /// <summary>
    /// Gets the base title shown for the asset tab.
    /// </summary>
    public string Title
    {
        get;
        private set
        {
            if (string.Equals(field, value, StringComparison.Ordinal))
            {
                return;
            }

            field = value;
            OnPropertyChanged(nameof(Title));
            OnPropertyChanged(nameof(DisplayTitle));
        }
    } = "<assets>";

    /// <summary>
    /// Gets whether the wrapped asset has unsaved changes.
    /// </summary>
    public bool IsDirty => Asset?.IsModified == true;

    /// <summary>
    /// Gets the title shown in the tab, including the dirty marker.
    /// </summary>
    public string DisplayTitle => IsDirty ? $"*{Title}" : Title;

    /// <summary>
    /// Occurs when a property value changes.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Initializes a new instance of the <see cref="AssetWrapper"/> class.
    /// </summary>
    /// <param name="asset">The asset to wrap.</param>
    public AssetWrapper(Asset? asset)
    {
        Asset = asset;
        RefreshFromAsset();

        if (Asset is not null)
        {
            Asset.StateChanged += HandleAssetStateChanged;
        }
    }

    void HandleAssetStateChanged(Asset _) => RefreshFromAsset();

    /// <summary>
    /// Refreshes the wrapper state from the underlying asset.
    /// </summary>
    public void RefreshFromAsset()
    {
        var newTitle = Asset?.DisplayName;
        Title = string.IsNullOrWhiteSpace(newTitle) ? "<assets>" : newTitle;

        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(DisplayTitle));
    }

    /// <inheritdoc/>
    public bool Equals(AssetWrapper? other) => Asset == other?.Asset;

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj switch
    {
        AssetWrapper other => Equals(other),
        _ => false,
    };

    /// <inheritdoc/>
    public override int GetHashCode() => Asset?.GetHashCode() ?? 0;

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Asset is not null)
        {
            Asset.StateChanged -= HandleAssetStateChanged;
        }
    }

    void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
