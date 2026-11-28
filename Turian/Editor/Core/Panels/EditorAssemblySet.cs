namespace Turian.Editor.Core;

/// <summary>
/// The assemblies whose editor contributions are in force: the project's scripts, without those of runtime bricks, and
/// the prebuilt assemblies of the enabled editor bricks.
/// </summary>
/// <param name="buildManager">Supplies the user assemblies and the loaded brick assemblies.</param>
[InternalService(InternalServiceLifetime.Singleton)]
public sealed class EditorAssemblySet(BuildManager buildManager)
{
    (int Ownership, int Loaded) generation = (-1, -1);
    ReflectionAssembly[] editorAssemblies = [];
    IReadOnlyList<ReflectionAssembly>? userAssemblies;
    ReflectionAssembly[] current = [];

    /// <summary>The assemblies in force; the same list is returned until one joins or leaves.</summary>
    public IReadOnlyList<ReflectionAssembly> Current
    {
        get
        {
            var latest = (Ownership: BrickAssemblies.EditorGeneration, Loaded: BrickAssemblies.Generation);
            var users = buildManager.PanelUserAssemblies;
            if (latest == generation && ReferenceEquals(users, userAssemblies))
            {
                return current;
            }

            if (latest != generation)
            {
                editorAssemblies = [.. buildManager.LoadedAssemblies.Where(BrickAssemblies.IsEditorAssembly)];
                generation = latest;
            }

            userAssemblies = users;
            ReflectionAssembly[] next = [.. users.Concat(editorAssemblies).Distinct()];
            if (!next.SequenceEqual(current))
            {
                current = next;
            }

            return current;
        }
    }
}
