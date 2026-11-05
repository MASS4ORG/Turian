using System.CommandLine;
using Turian.Cli.Sdk;
using Turian.Editor.CLI;

namespace Turian.Tests;

/// <summary>Command discovery, shared groups and isolation of invalid brick providers.</summary>
[Collection(SerialTests.Name)]
public sealed class CliPluginTests
{
    /// <summary>Several bricks can contribute commands to one group and execute their own actions.</summary>
    [Fact]
    public async Task PluginsShareGroupsAndExecute()
    {
        var root = new RootCommand();
        var registry = new CliPlugins.CommandRegistry(root);
        var invoked = false;
        var unity = new Command("unitypackage");
        unity.SetAction(_ => invoked = true);
        registry.Add([new CliCommand(unity, "import")]);
        registry.Add([new CliCommand(new Command("other"), "import")]);

        Assert.Equal(0, await root.Parse(["import", "unitypackage"]).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(invoked);
        Assert.Equal(["unitypackage", "other"], Assert.Single(root.Subcommands).Subcommands.Select(static command => command.Name));
    }

    /// <summary>A conflicting registration leaves the whole provider unapplied, including new groups.</summary>
    [Fact]
    public void ConflictsDoNotPartiallyRegisterOrReplaceBuiltins()
    {
        var builtin = new Command("brick");
        builtin.Aliases.Add("bricks");
        var root = new RootCommand { builtin };
        var registry = new CliPlugins.CommandRegistry(root);

        Assert.Throws<InvalidOperationException>(() => registry.Add(
            [new CliCommand(new Command("first"), "tools"), new CliCommand(new Command("bricks"))]));
        Assert.Same(builtin, Assert.Single(root.Subcommands));
        Assert.Throws<InvalidOperationException>(() => registry.Add([new CliCommand(new Command("extra"), "brick")]));
        Assert.Empty(builtin.Subcommands);
    }

    /// <summary>Two providers cannot claim the same command or alias.</summary>
    [Fact]
    public void DuplicatePluginNamesAreRejected()
    {
        var root = new RootCommand();
        var registry = new CliPlugins.CommandRegistry(root);
        var first = new Command("first");
        first.Aliases.Add("alias");
        registry.Add([new CliCommand(first, "tools")]);

        Assert.Throws<InvalidOperationException>(() => registry.Add([new CliCommand(new Command("alias"), "tools")]));
        Assert.Same(first, Assert.Single(Assert.Single(root.Subcommands).Subcommands));
    }

    /// <summary>A broken provider does not prevent subsequent providers or built-in commands from loading.</summary>
    [Fact]
    public void DiscoveryIsolatesBrokenProvidersAndPassesContext()
    {
        var root = new RootCommand { new Command("brick") };
        var context = new CliPluginContext("/brick", "/project", NullLogger.Instance);
        CliPlugins.RegisterAssembly(new CliPlugins.CommandRegistry(root), typeof(CliPluginTests).Assembly, context);

        Assert.Contains(root.Subcommands, static command => command.Name == "brick");
        Assert.Contains(root.Subcommands, static command => command.Name == "context" && command.Description == "/brick|/project");
        Assert.DoesNotContain(root.Subcommands, static command => command.Name == "partial");
    }

    /// <summary>The bootstrap selector accepts both option spellings and stops at the argument terminator.</summary>
    [Fact]
    public void ExplicitProjectSelectionIsParsedBeforeCommandDiscovery()
    {
        var directory = Path.GetTempPath();
        var project = Path.Combine(directory, "plugin-project");
        Assert.Equal(project, CliPlugins.FindProject(["ui", "--cli-project", project], directory));
        Assert.Equal(project, CliPlugins.FindProject([$"--cli-project={project}", "ui"], directory));
        Assert.Null(CliPlugins.FindProject(["--", "--cli-project", project], directory));
    }

    /// <summary>Installed payloads are discovered before parsing, with project installations preferred over studio ones.</summary>
    [Fact]
    public void InstalledBricksContributeCommandsBeforeParsing()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"turian-cli-plugins-{Guid.NewGuid():N}");
        try
        {
            var project = Path.Combine(temporary, "project");
            var studio = Path.Combine(temporary, "studio");
            var projectBrick = InstallFixture(project);
            var studioBrick = InstallFixture(studio);
            var workspace = new StudioBricks(studio, TurianStudioBricks.Hosts);
            var root = new RootCommand();
            CliPlugins.Load(root, [], NullLogger.Instance, workspace, project);
            var command = Assert.Single(root.Subcommands);
            Assert.Equal($"{projectBrick}|{project}", command.Description);
            Assert.Empty(root.Parse(["context"]).Errors);

            var outside = new RootCommand();
            CliPlugins.Load(outside, [], NullLogger.Instance, workspace, temporary);
            Assert.Equal($"{studioBrick}|", Assert.Single(outside.Subcommands).Description);
        }
        finally
        {
            ProjectPackages.Invalidate(Path.Combine(temporary, "project"));
            Directory.Delete(temporary, recursive: true);
        }
    }

    static string InstallFixture(string workspace)
    {
        const string id = "user.test.cli";
        var brick = Path.Combine(workspace, "plugin");
        var payload = Path.Combine(brick, "Precast~", "editor");
        Directory.CreateDirectory(payload);
        new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse("1.0.0"),
            Scopes = [PackageScope.Project, PackageScope.Studio],
        }.Save(brick);
        File.Copy(typeof(CliPluginTests).Assembly.Location, Path.Combine(payload, "Turian.Tests.dll"));
        var manifest = new ProjectManifest();
        manifest.Dependencies[id] = $"file:{brick}";
        manifest.Dependencies[ProjectPackages.RenderBrick] = null;
        manifest.Save(workspace);
        return brick;
    }

    /// <summary>An invalid project manifest does not hide commands installed into the studio.</summary>
    [Fact]
    public void ResolutionFailureLeavesStudioCommandsAvailable()
    {
        var temporary = Path.Combine(Path.GetTempPath(), $"turian-cli-failure-{Guid.NewGuid():N}");
        var project = Path.Combine(temporary, "project");
        try
        {
            var studio = Path.Combine(temporary, "studio");
            var brick = InstallFixture(studio);
            Directory.CreateDirectory(Path.Combine(project, "Bricks"));
            File.WriteAllText(Path.Combine(project, "Bricks", "manifest.json"), "{");
            var root = new RootCommand();
            CliPlugins.Load(root, [], NullLogger.Instance, new StudioBricks(studio, TurianStudioBricks.Hosts), project);
            Assert.Equal($"{brick}|{project}", Assert.Single(root.Subcommands).Description);
        }
        finally
        {
            ProjectPackages.Invalidate(project);
            Directory.Delete(temporary, recursive: true);
        }
    }

    /// <summary>A test provider that fails after yielding a command.</summary>
    public sealed class BrokenPlugin : ICliPlugin
    {
        /// <inheritdoc/>
        public IEnumerable<CliCommand> CreateCommands(CliPluginContext context)
        {
            yield return new CliCommand(new Command("partial"));
            throw new InvalidOperationException("Unavailable plugin dependency");
        }
    }

    /// <summary>A test provider that exposes its context in command metadata.</summary>
    public sealed class ContextPlugin : ICliPlugin
    {
        /// <inheritdoc/>
        public IEnumerable<CliCommand> CreateCommands(CliPluginContext context) =>
            [new CliCommand(new Command("context", $"{context.BrickRoot}|{context.ProjectRoot}"))];
    }
}
