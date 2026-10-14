namespace Turian.Tests;

/// <summary>Walks an assembly's references transitively so dependency-boundary tests see indirect dependencies.</summary>
static class AssemblyClosure
{
    /// <summary>
    /// Every assembly reachable from <paramref name="root"/>, keyed by simple name and mapped to the reference path
    /// that first reached it (<c>Root → A → B</c>). Assemblies the runtime cannot resolve are listed but not walked.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Of(Assembly root)
    {
        var rootName = root.GetName().Name!;
        var paths = new Dictionary<string, string>(StringComparer.Ordinal) { [rootName] = rootName };
        var pending = new Queue<(Assembly Assembly, string Path)>([(root, rootName)]);

        while (pending.TryDequeue(out var current))
        {
            foreach (var reference in current.Assembly.GetReferencedAssemblies())
            {
                var name = reference.Name!;
                if (paths.ContainsKey(name)) continue;

                var path = $"{current.Path} → {name}";
                paths[name] = path;
                if (TryLoad(reference) is { } assembly) pending.Enqueue((assembly, path));
            }
        }

        return paths;
    }

    /// <summary>The reference paths from <paramref name="root"/> to every reachable assembly matching <paramref name="forbidden"/>.</summary>
    public static string[] Offenders(Assembly root, Func<string, bool> forbidden) =>
        [.. Of(root).Where(entry => forbidden(entry.Key)).Select(static entry => entry.Value).Order(StringComparer.Ordinal)];

    static Assembly? TryLoad(AssemblyName reference)
    {
        try
        {
            return Assembly.Load(reference);
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or BadImageFormatException)
        {
            return null;
        }
    }
}
