using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using MASS4.Bricks;

namespace Turian.Editor.Core;

// Client-only content can be a codec or another optional feature, rather than a window host.
static class RuntimeHostMetadata
{
    internal static bool ProvidesHost(ResolvedPackage brick)
    {
        foreach (var path in BrickAssemblies.RuntimeAssemblies(brick))
        {
            using var input = File.OpenRead(path);
            using var pe = new PEReader(input);
            if (!pe.HasMetadata) { continue; }
            var metadata = pe.GetMetadataReader();
            foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
            {
                var attribute = metadata.GetCustomAttribute(handle);
                if (attribute.Constructor.Kind != HandleKind.MemberReference) { continue; }
                var parent = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent;
                if (parent.Kind != HandleKind.TypeReference) { continue; }
                var type = metadata.GetTypeReference((TypeReferenceHandle)parent);
                if (metadata.GetString(type.Namespace) == "Turian.Engine.Core" &&
                    metadata.GetString(type.Name) is "ClientRuntimeHostAttribute" or "GamePlayerAttribute")
                {
                    return true;
                }
            }
        }
        var roots = BrickLayout.PathsOf(brick.RootPath, brick.Manifest, "source")
            .Prepend(Path.Combine(brick.RootPath, "Runtime")).Distinct(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) { continue; }
            foreach (var path in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetRelativePath(root, path).Split(Path.DirectorySeparatorChar)
                    .Any(static part => part is "bin" or "obj")) { continue; }
                var syntax = CSharpSyntaxTree.ParseText(File.ReadAllText(path)).GetCompilationUnitRoot();
                foreach (var list in syntax.AttributeLists)
                {
                    if (list.Target?.Identifier.ValueText != "assembly") { continue; }
                    foreach (var attribute in list.Attributes)
                    {
                        var name = attribute.Name switch
                        {
                            QualifiedNameSyntax qualified => qualified.Right.Identifier.ValueText,
                            AliasQualifiedNameSyntax alias => alias.Name.Identifier.ValueText,
                            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                            _ => string.Empty,
                        };
                        if (name is "ClientRuntimeHost" or "ClientRuntimeHostAttribute" or "GamePlayer" or "GamePlayerAttribute")
                        {
                            return true;
                        }
                    }
                }
            }
        }
        return false;
    }
}
