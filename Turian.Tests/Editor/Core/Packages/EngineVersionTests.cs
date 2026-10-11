namespace Turian.Tests;

/// <summary>The engine reports the version its project declares, so local builds stamp projects with it.</summary>
public sealed class EngineVersionTests
{
    /// <summary>The engine version is the <c>Version</c> of the engine's csproj, not the SDK's 1.0.0.</summary>
    [Fact]
    public void EngineVersionIsTheProjectVersion()
    {
        var root = AppContext.BaseDirectory;
        while (Directory.GetFiles(root, "*.slnx").Length == 0) root = Directory.GetParent(root)!.FullName;
        var project = XDocument.Load(Path.Combine(root, "Turian/Engine/Core/Turian.Engine.Core.csproj"));
        var declared = project.Descendants("Version").Single().Value;

        Assert.Equal(declared, ProjectSettingsFiles.EngineVersionStamp);
    }
}
