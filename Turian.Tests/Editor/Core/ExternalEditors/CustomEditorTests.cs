namespace Turian.Tests;

/// <summary>The custom editor starts the user's executable with their argument template.</summary>
public sealed class CustomEditorTests
{
    static readonly CustomEditor Editor = new();

    /// <summary>It has nothing to discover until a program is chosen.</summary>
    [Fact]
    public void HasNoInstallationsOfItsOwn() => Assert.Empty(Editor.Discover());

    /// <summary>The executable and template come from the installation, with paths kept as single arguments.</summary>
    [Theory]
    [InlineData(@"C:\Program Files\Ünïcode Editor\edit.exe", @"C:\Games\Mein Spiel", @"C:\Games\Mein Spiel\Assets\Ä.cs")]
    [InlineData("/usr/local/bin/my editor", "/home/zoë/game", "/home/zoë/game/Assets/Ä.cs")]
    [InlineData("/Applications/Edit.app/Contents/MacOS/edit", "/Users/李雷/My Game", "/Users/李雷/My Game/Assets/Ä.cs")]
    public void BuildsTheCommandFromTheTemplate(string executable, string project, string file)
    {
        var installation = CustomEditor.Installation(executable, "--project {projectDir} --at {file}:{line}");

        var command = Editor.BuildCommand(installation, new EditorContext(project, File: file, Line: 7));

        Assert.Equal(executable, command.FileName);
        Assert.Equal(["--project", project, "--at", $"{file}:7"], command.Arguments);
    }

    /// <summary>Without a template the file is passed on its own, or the project folder when only the project opens.</summary>
    [Fact]
    public void DefaultTemplateOpensTheFileOrTheProject()
    {
        var installation = CustomEditor.Installation("/bin/edit", null);

        Assert.Equal(["/p/a.cs"],
            Editor.BuildCommand(installation, new EditorContext("/p", File: "/p/a.cs")).Arguments);
        Assert.Equal(["/p"], Editor.BuildCommand(installation, new EditorContext("/p")).Arguments);
    }

    /// <summary>A template whose arguments all need a missing value falls back to the project folder.</summary>
    [Fact]
    public void ProjectOnlyOpenFallsBackToTheProjectFolder()
    {
        var installation = CustomEditor.Installation("/bin/edit", "--goto {file}:{line}");

        Assert.Equal(["/p"], Editor.BuildCommand(installation, new EditorContext("/p")).Arguments);
    }

    /// <summary>The installation is named after the program.</summary>
    [Fact]
    public void InstallationIsNamedAfterTheProgram()
    {
        var installation = CustomEditor.Installation("/bin/my-edit", "{file}");

        Assert.Equal("my-edit", installation.DisplayName);
        Assert.Equal(CustomEditor.EditorId, installation.EditorId);
    }
}
