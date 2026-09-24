namespace Turian.Engine.Core;

/// <summary>
/// One aspect of a project's configuration — the player, input, graphics — kept as a data asset of its
/// own rather than as a section of a central file. Every data asset of such a class under <c>Assets</c>
/// is one; each is edited in the inspector like any other data asset and read through
/// <see cref="IAppSettings.Get{T}"/>. A project with none of a kind reads that kind's declared defaults.
/// </summary>
public abstract class ProjectSettingsAsset : DataAsset
{
}
