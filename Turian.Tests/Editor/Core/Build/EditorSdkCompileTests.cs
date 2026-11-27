using Microsoft.Build.Construction;

namespace Turian.Tests;

/// <summary>Editor code compiled from sources, in a project or a source brick, can use what the editor offers.</summary>
[Collection(SerialTests.Name)]
public sealed class EditorSdkCompileTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-editor-sdk-").FullName;

    /// <summary>Removes this test's isolated build output.</summary>
    public void Dispose() => Directory.Delete(root, true);

    /// <summary>An editor assembly compiles a live plugin, a panel drawing editor widgets and inspector options.</summary>
    [Fact]
    [Trait("Category", "E2E")]
    public async Task AnEditorAssemblyCompilesAgainstTheEditorSdk()
    {
        var settings = new BuildAppSettings { ProjectAbsoluteDir = root, Title = "Example" };
        settings.Get<PlayerSettings>().StartupScene = new AssetReference<Prefab>(Guid.NewGuid());
        var editor = Path.Combine(settings.AssetsAbsoluteDir, "Editor");
        Directory.CreateDirectory(editor);
        var definition = Path.Combine(editor, "Tools.dataasset");
        var id = Guid.NewGuid();
        await File.WriteAllTextAsync(definition,
            $$"""{ "__TypeId": "{{AssemblyDefinition.TypeIdValue}}", "Name": "Tools", "EditorOnly": true, "Id": "{{id}}" }""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync($"{definition}.meta",
            $$"""{ "__TypeId": "aab4f92b-7216-52d8-b722-7399613c929c", "RelativePath": "Assets/Editor/Tools.dataasset", "Id": "{{id}}" }""",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(editor, "Plugin.cs"), """
            using Gaya.Sdk;
            using Guinevere;

            namespace Example.Tools;

            public sealed class Plugin : ILivePlugin, IPanel
            {
                public System.IDisposable Activate(LivePluginContext context) => null!;
                public void Render(Gui gui) => gui.Tooltip(gui.CurrentNode, "tip");
                public object Options => Turian.Editor.Core.InspectorForms.Options();
            }
            """, TestContext.Current.CancellationToken);
        var output = Path.Combine(root, ".Cache", "bin", "SlotA");

        await new CompileUserCode(settings, NullLogger.Instance, output).ExecuteAsync();

        Assert.True(File.Exists(Path.Combine(output, "Tools.dll")));
    }

    /// <summary>A library the project already references or takes as a package, or the host lacks, is not added.</summary>
    [Fact]
    public void HostLibrariesNeverDuplicateOwnReferences()
    {
        foreach (var name in new[] { "Gaya.Sdk", "Autoformers", "Guinevere" })
        {
            File.WriteAllText(Path.Combine(root, $"{name}.dll"), string.Empty);
        }

        var project = ProjectRootElement.Create(Path.Combine(root, "tools.csproj"));
        var own = project.AddItemGroup();
        own.AddItem("PackageReference", "MASS4.Guinevere");
        own.AddItem("Reference", "Autoformers");

        CsProjectGenerator.AddEditorHostReferences(project, root);

        Assert.Equal(["Autoformers", "Gaya.Sdk"],
            project.Items.Where(static item => item.ItemType == "Reference").Select(static item => item.Include));
        var added = project.Items.Single(static item => item.Include == "Gaya.Sdk");
        Assert.Equal(Path.Combine(root, "Gaya.Sdk.dll"),
            added.Metadata.Single(static data => data.Name == "HintPath").Value);
    }
}
