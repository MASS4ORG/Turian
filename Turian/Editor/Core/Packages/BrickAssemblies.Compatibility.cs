using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Turian.Editor.Core;

public static partial class BrickAssemblies
{
    static void VerifyLoadedDependencies(string path)
    {
        var loaded = AssemblyLoadContext.Default.Assemblies.Where(static assembly => !assembly.IsDynamic)
            .Select(static assembly => assembly.GetName())
            .Where(static name => name.Name is not null)
            .ToDictionary(static name => name.Name!, StringComparer.OrdinalIgnoreCase);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var reference = metadata.GetAssemblyReference(handle);
            var name = metadata.GetString(reference.Name);
            if (loaded.TryGetValue(name, out var host) && host.Version < reference.Version)
            {
                throw new FileLoadException(
                    $"The brick requires {name} {reference.Version}, but the host already loaded {host.Version}. "
                    + "Rebuild the brick and host with matching dependencies, then restart the host.", path);
            }
        }
    }
}
