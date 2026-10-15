namespace Turian.Tests;

/// <summary>Version unification across bricks' NuGet maps, per the brick dependency policy.</summary>
public sealed class BrickNuGetTests
{
    /// <summary>Two bricks asking for different versions of one package load the highest within a major.</summary>
    [Fact]
    public void HighestVersionWithinAMajorWins()
    {
        var unified = BrickNuGet.UnifyVersions([Brick("a.one", ("X", "1.2.0")), Brick("a.two", ("X", "1.4.0"))]);

        Assert.Equal("1.4.0", unified["X"]);
    }

    /// <summary>Different major versions are a hard error naming both bricks and both versions.</summary>
    [Fact]
    public void DifferentMajorsFailNamingBothBricks()
    {
        var error = Assert.Throws<InvalidOperationException>(static () =>
            BrickNuGet.UnifyVersions([Brick("a.one", ("X", "1.4.0")), Brick("a.two", ("X", "2.0.0"))]));

        Assert.Contains("a.one", error.Message);
        Assert.Contains("a.two", error.Message);
        Assert.Contains("1.4.0", error.Message);
        Assert.Contains("2.0.0", error.Message);
    }

    /// <summary>Prerelease ordering follows NuGet SemVer: a later prerelease and a release beat earlier labels.</summary>
    [Fact]
    public void PrereleaseOrderingFollowsNuGetSemVer()
    {
        var unified = BrickNuGet.UnifyVersions([Brick("a.one", ("X", "2.5.0-beta.9")), Brick("a.two", ("X", "2.5.0-beta.33"))]);

        Assert.Equal("2.5.0-beta.33", unified["X"]);
    }

    /// <summary>A floating version in a brick's map fails and names the brick.</summary>
    [Fact]
    public void FloatingVersionsAreRejected()
    {
        var error = Assert.Throws<InvalidOperationException>(static () =>
            BrickNuGet.UnifyVersions([Brick("a.one", ("X", "6.*"))]));

        Assert.Contains("a.one", error.Message);
        Assert.Contains("6.*", error.Message);
    }

    /// <summary>Platform-specific entries unify for their platform only.</summary>
    [Fact]
    public void PlatformEntriesUnifyForTheirPlatformOnly()
    {
        ResolvedPackage[] bricks = [Brick("a.one", ("Common", "1.0.0"), platformNuget: ("android", "Native", "2.0.0"))];

        Assert.DoesNotContain("Native", BrickNuGet.UnifyVersions(bricks, "desktop").Keys);
        Assert.Equal("2.0.0", BrickNuGet.UnifyVersions(bricks, "android")["Native"]);
    }

    static ResolvedPackage Brick(string id, (string Package, string Version) nuget, (string Platform, string Package, string Version)? platformNuget = null)
    {
        var manifest = new PackageManifest { Name = id, Version = SemanticVersion.Parse("0.1.0") };
        manifest.Nuget[nuget.Package] = nuget.Version;
        if (platformNuget is { } extra)
        {
            manifest.PlatformNuget[extra.Platform] = new Dictionary<string, string> { [extra.Package] = extra.Version };
        }

        return new ResolvedPackage(id, manifest, "/", PackageOrigin.Builtin, $"builtin:{id}", null, null, 1, false);
    }
}
