namespace Gaya.Tests;

/// <summary>Live plugins follow changes to the open project's brick snapshot.</summary>
public sealed class LiveProjectPluginsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), $"gaya-live-{Guid.NewGuid():N}");

    /// <inheritdoc />
    public void Dispose() => TestFiles.Delete(root);

    /// <summary>A compiled project plugin contributes and removes a command in the same running application.</summary>
    [Fact]
    public void ProjectSnapshotActivatesAndRemovesPlugin()
    {
        const string id = "com.acme.live";
        const string command = "com.acme.live.command";
        var folder = Path.Combine(root, "plugin");
        var library = Path.Combine(folder, StudioBricks.LibraryDirectoryName);
        Directory.CreateDirectory(library);
        var manifest = new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse("1.0.0"),
            Scopes = [PackageScope.Studio],
            LivePlugin = true,
        };
        manifest.Save(folder);
        var source = """
            public sealed class Plugin : Gaya.Sdk.ILivePlugin
            {
                public System.IDisposable Activate(Gaya.Sdk.LivePluginContext context)
                {
                    context.Commands.Register(new Gaya.Sdk.CommandDescriptor("com.acme.live.command", "Live", _ => { }));
                    return new Lifetime(context.Commands);
                }
                sealed class Lifetime(Gaya.Sdk.ICommandRegistry commands) : System.IDisposable
                {
                    public void Dispose() => commands.Remove("com.acme.live.command");
                }
            }
            """;
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(static path => MetadataReference.CreateFromFile(path))
            .Append(MetadataReference.CreateFromFile(typeof(ILivePlugin).Assembly.Location));
        var assemblyName = $"Live{Guid.NewGuid():N}";
        var ct = TestContext.Current.CancellationToken;
        var compilation = CSharpCompilation.Create(assemblyName, [CSharpSyntaxTree.ParseText(source, cancellationToken: ct)],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var result = compilation.Emit(Path.Combine(library, $"{assemblyName}.dll"), cancellationToken: ct);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var snapshot = new ProjectSnapshot();
        var studio = new StudioBricks(Path.Combine(root, "studio"), new Dictionary<string, SemanticVersion>(),
            new PackageStore(Path.Combine(root, "store")));
        using var app = PluginHost.Load([], NullLogger.Instance, services =>
        {
            services.AddSingleton(studio);
            services.AddSingleton<IProjectEditorBricks>(snapshot);
        });
        Assert.Null(app.Commands.Find(command));

        snapshot.EnabledBricks = [new ResolvedPackage(id, manifest, folder, PackageOrigin.File, "file:plugin",
            null, null, 1, false)];
        Refresh(app);
        Assert.NotNull(app.Commands.Find(command));

        snapshot.EnabledBricks = [];
        Refresh(app);
        Assert.Null(app.Commands.Find(command));
    }

    static void Refresh(GayaApplication app)
    {
        var host = typeof(GayaApplication).GetProperty("LivePlugins", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(app)!;
        host.GetType().GetMethod("Refresh", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(host, null);
    }

    sealed class ProjectSnapshot : IProjectEditorBricks
    {
        public IReadOnlyList<ResolvedPackage> EnabledBricks { get; set; } = [];
    }
}
