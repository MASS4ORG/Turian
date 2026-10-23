namespace Turian.Tests;

/// <summary>The CLI identifies editable, linked and baked brick forms in its user-facing output.</summary>
[Collection(SerialTests.Name)]
public sealed class BrickCliFormsTests
{
    /// <summary>The form listing includes local edits and links while omitting built-in implementation bricks.</summary>
    [Fact]
    public void FormOutputIdentifiesEveryDevelopmentForm()
    {
        var packages = new[]
        {
            Package("com.acme.source", PackageOrigin.Registry),
            Package("com.acme.edit", PackageOrigin.Embedded),
            Package("com.acme.link", PackageOrigin.Link),
            Package("com.acme.source.bake", PackageOrigin.Registry),
            Package("com.acme.host", PackageOrigin.Builtin),
        };
        packages[3].Manifest.BakeOf = new PackageBakeOf
        {
            Brick = "com.acme.baked",
            Version = SemanticVersion.Parse("1.0.0"),
            Integrity = "sha256-" + Convert.ToBase64String(new byte[32]),
        };
        using var output = new StringWriter();
        var previous = Console.Out;
        try
        {
            Console.SetOut(output);
            var method = typeof(global::Turian.Editor.CLI.Program).GetMethod("PrintForms",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            method.Invoke(null, [new PackageResolution(packages, new LockFile())]);
        }
        finally
        {
            Console.SetOut(previous);
        }
        var text = output.ToString();
        Assert.Contains("com.acme.source 1.0.0: source", text, StringComparison.Ordinal);
        Assert.Contains("source, editable in Bricks/com.acme.edit", text, StringComparison.Ordinal);
        Assert.Contains("source, linked to", text, StringComparison.Ordinal);
        Assert.Contains("com.acme.baked 1.0.0: twin com.acme.source.bake", text, StringComparison.Ordinal);
        Assert.DoesNotContain("com.acme.host", text, StringComparison.Ordinal);
    }

    static ResolvedPackage Package(string id, PackageOrigin origin) =>
        new(id, new PackageManifest { Name = id, Version = SemanticVersion.Parse("1.0.0") },
            Path.Combine("project", id), origin, "local fixture", null, null, 1, false);
}
