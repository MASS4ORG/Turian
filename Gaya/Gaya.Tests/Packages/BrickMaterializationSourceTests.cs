namespace Gaya.Tests;

/// <summary>Typed producer dependencies require portable destinations and immutable source pins.</summary>
public sealed class BrickMaterializationSourceTests
{
    /// <summary>Store references and existing GitHub companions normalize into typed dependencies.</summary>
    [Theory]
    [InlineData("org.mass4.demo@1.2.3", "store", null)]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.2.3-rc.1","path":"demo"}""", "store", null)]
    [InlineData("""{"repository":"MASS4ORG/demo","ref":"v1.2.3","path":"demo"}""", "git", "https://github.com/MASS4ORG/demo.git")]
    [InlineData("""{"type":"git","url":"https://gitlab.com/team/demo.git","ref":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","path":"demo"}""", "git", "https://gitlab.com/team/demo.git")]
    [InlineData("""{"type":"archive","url":"https://example.com/demo.tar.gz","sha256":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","path":"demo"}""", "archive", "https://example.com/demo.tar.gz")]
    public void PinnedSourcesAreAccepted(string text, string type, string? url)
    {
        var source = BrickMaterializationSource.Parse(text);
        Assert.Equal(type, source.Type);
        Assert.Equal(url, source.Url);
    }

    /// <summary>Moving refs, absent checksums, ambiguous kinds and escaping paths fail before fetching.</summary>
    [Theory]
    [InlineData("org.mass4.demo")]
    [InlineData("org.mass4.demo@^1.0.0")]
    [InlineData("invalid@1.0.0")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0"}""")]
    [InlineData("""{"id":"org.mass4.demo","version":"1.0.0","path":"demo"}""")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0","path":"../demo"}""")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0","path":"/demo"}""")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0","path":"a\\demo"}""")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0","path":"C:demo"}""")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0","path":"a//demo"}""")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0","path":"brick/new"}""")]
    [InlineData("""{"type":"git","url":"https://gitlab.com/team/demo.git","ref":"main","path":"demo"}""")]
    [InlineData("""{"type":"git","url":"https://gitlab.com/team/demo.git","ref":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","path":"demo","id":"com.example.demo"}""")]
    [InlineData("""{"type":"git","url":"https://gitlab.com/team/demo.git","ref":"v1.0.0","path":"demo"}""")]
    [InlineData("""{"repository":"MASS4ORG/demo","ref":"main","path":"demo"}""")]
    [InlineData("""{"repository":"../demo","ref":"v1.0.0","path":"demo"}""")]
    [InlineData("""{"type":"git","repository":"MASS4ORG/demo","url":"https://github.com/MASS4ORG/demo","ref":"v1.0.0","path":"demo"}""")]
    [InlineData("""{"type":"git","url":"ssh://gitlab.com/team/demo.git","ref":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","path":"demo"}""")]
    [InlineData("""{"type":"git","url":"https://user:secret@gitlab.com/team/demo.git","ref":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","path":"demo"}""")]
    [InlineData("""{"type":"git","url":"https://gitlab.com/team/demo.git?token=x","ref":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","path":"demo"}""")]
    [InlineData("""{"type":"archive","url":"https://example.com/demo.zip","path":"demo"}""")]
    [InlineData("""{"type":"archive","url":"https://example.com/demo.zip","sha256":"short","path":"demo"}""")]
    [InlineData("""{"type":"svn","path":"demo"}""")]
    [InlineData("""{"type":"store","id":"org.mass4.demo","version":"1.0.0","path":"demo","unknown":true}""")]
    [InlineData("{broken")]
    public void InvalidSourcesAreRejected(string text) =>
        Assert.Throws<PackageException>(() => BrickMaterializationSource.Parse(text));
}
