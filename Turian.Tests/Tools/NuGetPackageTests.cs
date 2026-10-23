namespace Turian.Tests;

/// <summary>Checks the NuGet distribution boundary of the solution.</summary>
public sealed class NuGetPackageTests
{
    /// <summary>Only shared contracts and the engine API are published; hosts and editors ship as DLLs.</summary>
    [Fact]
    public void NuGetPublication_ContainsOnlyPublicLibraries()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Turian.slnx")))
        {
            root = root.Parent;
        }

        Assert.NotNull(root);
        var solution = XDocument.Load(Path.Combine(root.FullName, "Turian.slnx"));
        var packageIds = solution.Descendants("Project")
            .Select(project => Path.Combine(root.FullName, project.Attribute("Path")!.Value))
            .SelectMany(static path => XDocument.Load(path).Descendants("PackageId")
                .Select(static package => package.Value))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["MASS4.Bricks", "MASS4.Gaya.Attributes", "MASS4.Gaya.Sdk", "MASS4.Turian.Attributes", "MASS4.Turian.Engine"],
            packageIds);
    }
}
