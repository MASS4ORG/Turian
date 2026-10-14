namespace Turian.Engine.Core;

/// <summary>
/// Utility class for file-related operations.
/// </summary>
public static class FileUtil
{
    /// <summary>
    /// Retrieves the bytes of a shader file embedded as a resource in the executing assembly.
    /// </summary>
    /// <param name="filename">The name of the shader file.</param>
    /// <param name="renderSystemName">The name of the render system (for error messages).</param>
    /// <returns>The bytes of the shader file.</returns>
    /// <exception cref="ApplicationException">Thrown when the shader file is not found.</exception>
    public static byte[] GetShaderBytes(string filename, string renderSystemName)
    {
        // Get the executing assembly.
        var assembly = Assembly.GetExecutingAssembly();

        // Find the resource name that matches the given filename.
        var resourceName =
            assembly.GetManifestResourceNames().FirstOrDefault(s => s.EndsWith(filename, StringComparison.InvariantCultureIgnoreCase))
            ?? throw new FileNotFoundException(
                $"*** In {renderSystemName}, No shader file found with name {filename}\n*** Check the resourceName and try again! Did you forget to set the glsl file to Embedded Resource/Do Not Copy?"
            );

        // Get the resource stream for the found resource name.
        using var stream =
            assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"*** In {renderSystemName}, No shader file found at {resourceName}\n*** Check the resourceName and try again! Did you forget to set the glsl file to Embedded Resource/Do Not Copy?"
            );

        // Copy the resource stream to a memory stream and return its byte array.
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
