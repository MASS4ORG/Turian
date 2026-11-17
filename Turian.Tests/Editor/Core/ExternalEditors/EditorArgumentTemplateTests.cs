namespace Turian.Tests;

/// <summary>Argument templates keep every path one argument, whatever characters it holds or OS it came from.</summary>
public sealed class EditorArgumentTemplateTests
{
    /// <summary>Paths with spaces, quotes and non-ASCII text stay whole in the shapes each OS produces.</summary>
    [Theory]
    [InlineData(@"C:\Users\José Ünal\My Game\Assets\Main Script.cs", @"C:\Users\José Ünal\My Game")]
    [InlineData("/home/zoë/my game/Assets/Main Script.cs", "/home/zoë/my game")]
    [InlineData("/Users/李雷/Projects/My Game/Assets/Main Script.cs", "/Users/李雷/Projects/My Game")]
    [InlineData("/home/o'brien/\"quoted\"/Assets/a.cs", "/home/o'brien/\"quoted\"")]
    public void PathsStayOneArgument(string file, string project)
    {
        var context = new EditorContext(project, File: file, Line: 12, Column: 4);

        var arguments = EditorArgumentTemplate.Expand("--folder \"{projectDir}\" --goto \"{file}:{line}:{column}\"", context);

        Assert.Equal(["--folder", project, "--goto", $"{file}:12:4"], arguments);
    }

    /// <summary>Tokens inside one argument are filled in without splitting it.</summary>
    [Fact]
    public void TokensInsideAnArgumentAreFilledInPlace()
    {
        var context = new EditorContext("/p q", Solution: "/p q/Game.slnx", File: "/p q/a.cs", Line: 3, Column: 9);

        Assert.Equal(["/p q/Game.slnx", "--line=3", "--column=9"],
            EditorArgumentTemplate.Expand("{solution} --line={line} --column={column}", context));
    }

    /// <summary>An argument that needs a value the context lacks is left out with its switch instead of passed empty.</summary>
    [Fact]
    public void ArgumentsWithoutAValueAreDropped()
    {
        var context = new EditorContext("/project");

        Assert.Equal(["--folder", "/project"],
            EditorArgumentTemplate.Expand("--folder {projectDir} --goto {file}:{line} {solution}", context));
    }

    /// <summary>The line and column default to the first.</summary>
    [Fact]
    public void LineAndColumnDefaultToOne()
    {
        var context = new EditorContext("/project", File: "/project/a.cs");

        Assert.Equal(["/project/a.cs:1:1"], EditorArgumentTemplate.Expand("{file}:{line}:{column}", context));
    }

    /// <summary>A quoted empty argument is kept and unknown tokens are left as written.</summary>
    [Fact]
    public void QuotedEmptyAndUnknownTokensAreKept()
    {
        var context = new EditorContext("/project");

        Assert.Equal(["", "{other}"], EditorArgumentTemplate.Expand("\"\" {other}", context));
        Assert.Empty(EditorArgumentTemplate.Expand(null, context));
    }

    /// <summary>Templates are validated against the known tokens.</summary>
    [Theory]
    [InlineData("{file}:{line}:{column} {projectDir} {solution}", true, null)]
    [InlineData("--x {files}", false, "{files}")]
    [InlineData("", true, null)]
    public void ValidatesTokens(string template, bool valid, string? unknown)
    {
        Assert.Equal(valid, EditorArgumentTemplate.IsValid(template, out var found));
        Assert.Equal(unknown, found);
    }
}
