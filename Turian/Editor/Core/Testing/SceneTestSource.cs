using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace Turian.Editor.Core;

/// <summary>Reads test declaration locations from the portable symbols produced with user tests.</summary>
static class SceneTestSource
{
    internal static (string? File, int Line) Read(MethodInfo method, string? symbolsDirectory)
    {
        var path = symbolsDirectory is null ? Path.ChangeExtension(method.Module.FullyQualifiedName, ".pdb")
            : Path.Combine(symbolsDirectory, method.Module.Assembly.GetName().Name + ".pdb");
        if (!File.Exists(path)) { return (null, 0); }
        try
        {
            using var stream = File.OpenRead(path);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(stream);
            var reader = provider.GetMetadataReader();
            var handle = MetadataTokens.MethodDefinitionHandle(method.MetadataToken & 0x00ffffff);
            var debug = reader.GetMethodDebugInformation(handle);
            foreach (var point in debug.GetSequencePoints())
            {
                if (point.IsHidden) { continue; }
                var document = reader.GetDocument(point.Document.IsNil ? debug.Document : point.Document);
                return (reader.GetString(document.Name), point.StartLine);
            }
        }
        catch (Exception error) when (error is IOException or BadImageFormatException or ArgumentException) { }
        return (null, 0);
    }
}
