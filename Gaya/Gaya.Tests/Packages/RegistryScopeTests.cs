using System.Text.Json;

namespace Gaya.Tests;

/// <summary>Optional registry scope filters apply consistently to configuration and brick names.</summary>
public sealed class RegistryScopeTests
{
    /// <summary>Empty scopes and an explicit wildcard accept all names; prefixes respect dot boundaries.</summary>
    [Theory]
    [InlineData(null, "com.acme.rules", true)]
    [InlineData(null, "org.mass4.gaya.theme", true)]
    [InlineData("*", "com.acme.rules", true)]
    [InlineData("*", "user.mateo.rules", true)]
    [InlineData("com.acme", "com.acme", true)]
    [InlineData("com.acme", "com.acme.rules", true)]
    [InlineData("com.acme", "com.acmex.rules", false)]
    [InlineData("com.acme", "org.mass4.gaya.theme", false)]
    public void ScopesFilterBrickNames(string? scope, string id, bool expected)
    {
        var registry = new ScopedRegistry { Scopes = scope is null ? [] : [scope] };

        Assert.Equal(expected, registry.Serves(id));
    }

    /// <summary>Omitting scopes from JSON accepts publishers without an explicit wildcard.</summary>
    [Fact]
    public void OmittedScopesAcceptAllNames()
    {
        var registry = JsonSerializer.Deserialize<ScopedRegistry>("{}");

        Assert.NotNull(registry);
        Assert.True(registry.Serves("com.newpublisher.rules"));
    }

    /// <summary>Each prefix is accepted independently, and a wildcard accepts names outside all prefixes.</summary>
    [Fact]
    public void MultipleScopesAreCombined()
    {
        var registry = new ScopedRegistry { Scopes = ["com.acme", "org.mass4"] };

        Assert.True(registry.Serves("com.acme.rules"));
        Assert.True(registry.Serves("org.mass4.gaya.theme"));
        Assert.False(registry.Serves("user.mateo.rules"));

        registry.Scopes.Add("*");
        Assert.True(registry.Serves("user.mateo.rules"));
    }

    /// <summary>An unfiltered registry still requires an identity, address and explicit signing trust.</summary>
    [Fact]
    public void UnfilteredRegistriesRequireTrust()
    {
        var registry = new ScopedRegistry { Name = "store", Url = "https://store.example/v1" };

        Assert.Contains("trusted key", Assert.Throws<PackageException>(() => BrickRegistries.Validate(registry)).Message,
            StringComparison.Ordinal);

        registry.AllowUnsigned = true;
        BrickRegistries.Validate(registry);

        registry.Scopes.Add(" ");
        Assert.Throws<PackageException>(() => BrickRegistries.Validate(registry));
    }
}
