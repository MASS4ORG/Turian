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

        packages[3].Manifest.Variant = "turian3.3";
        Assert.Contains("com.acme.baked 1.0.0: twin turian3.3", Capture("PrintForms", new PackageResolution(packages, new LockFile())).Out,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// An upload reports its variant, every lifecycle state, the build the store recorded and its warnings, which go
    /// to standard error so a workflow can lift them.
    /// </summary>
    [Fact]
    public void PublishedReleasesReportBuildStateAndWarnings()
    {
        var store = new StoreClient("http://store.example:5080", "token");
        var release = new StoreRelease(Guid.Empty, "com.acme.rules", "1.0.0", Approved: false, IsPrivate: true,
            DeprecationReason: "old", Variant: "turian3.3", Status: "quarantined",
            Build: new RegistryBuild("ghcr.io/example/cli@sha256:" + new string('a', 64), "3.3.0"),
            Warnings: ["sourceRevision differs"]);

        var (output, error) = Capture("ReportPublished", store, release);

        Assert.Contains("Published com.acme.rules 1.0.0 (turian3.3) to store.example  (awaiting approval, quarantined, private, deprecated: old)",
            output, StringComparison.Ordinal);
        Assert.Contains("Recorded build: ghcr.io/example/cli@sha256:", output, StringComparison.Ordinal);
        Assert.Contains("waits for the store's approval", output, StringComparison.Ordinal);
        Assert.Equal("warning: sourceRevision differs", error.Trim());

        var (plain, quiet) = Capture("ReportPublished", store, new StoreRelease(Guid.Empty, "com.acme.rules", "1.0.0", Approved: true,
            Yanked: true, Variant: PackageVariant.Source));
        Assert.Equal("Published com.acme.rules 1.0.0 to store.example  (yanked)", plain.Trim());
        Assert.Empty(quiet);

        var (offered, _) = Capture("ReportPublished", store, new StoreRelease(Guid.Empty, "com.acme.rules", "1.0.0", Approved: true));
        Assert.Equal("Published com.acme.rules 1.0.0 to store.example", offered.Trim());
    }

    static (string Out, string Error) Capture(string name, params object[] arguments)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var (previousOut, previousError) = (Console.Out, Console.Error);
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            typeof(global::Turian.Editor.CLI.Program).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, arguments);
        }
        finally
        {
            Console.SetOut(previousOut);
            Console.SetError(previousError);
        }

        return (output.ToString(), error.ToString());
    }

    static ResolvedPackage Package(string id, PackageOrigin origin) =>
        new(id, new PackageManifest { Name = id, Version = SemanticVersion.Parse("1.0.0") },
            Path.Combine("project", id), origin, "local fixture", null, null, 1, false);
}
