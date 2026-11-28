namespace Gaya.Plugin.Turian;

/// <summary>
/// An open workspace tab whose document an editor brick contributed. Its kind, and so its layout and panels, is the one
/// the brick's document reports.
/// </summary>
/// <param name="wrapper">The workspace tab.</param>
sealed class ContributedDocument(AssetWrapper wrapper) : IDocument
{
    /// <summary>The workspace tab this document stands for.</summary>
    public AssetWrapper Wrapper { get; } = wrapper;

    /// <inheritdoc />
    public string Id { get; } = wrapper.Id;

    /// <inheritdoc />
    public string Kind { get; } = wrapper.Kind;

    /// <inheritdoc />
    public string Title => Wrapper.Title;
}
