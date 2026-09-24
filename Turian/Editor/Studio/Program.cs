using var loggerFactory = LoggerFactory.Create(builder => builder
#if DEBUG
    .SetMinimumLevel(LogLevel.Debug)
#else
    .SetMinimumLevel(LogLevel.Information)
#endif
    .AddConsole()
    .AddProvider(LogBuffer.Provider));
Log.Configure(loggerFactory);

// Before any MSBuild type loads, and before a scene deserializes: user components would otherwise
// come back as MissingComponent.
BuildManager.MsBuildLocatorRegisterDefaults();
TypeRegistry.ScanAssembly(typeof(UiDocumentComponent).Assembly);

// Built-in plugins are compiled in; a plugins/ folder scan is added later.
var pluginAssemblies = new[] { typeof(GayaPlugin).Assembly };
var shell = new ShellHost();
var dispatcher = new CommandDispatcher();
var panelAccessor = new PanelAccessor();
using var app = PluginHost.Load(pluginAssemblies, Log.Logger,
    services =>
    {
        services.AddSingleton<IShellHost>(shell);
        services.AddSingleton<ICommandDispatcher>(dispatcher);
        services.AddSingleton<IPanelAccessor>(panelAccessor);
    },
    args);
using var workbench = new Workbench(app);
dispatcher.Bind(workbench);
panelAccessor.Bind(workbench);
shell.PanelRequested += workbench.ShowPanel;
shell.CommandPaletteRequested += workbench.ToggleCommandPalette;

// Headless: `--dump <file.png> [WxH]` renders one workbench frame and exits (CI / visual review).
if (args.Length >= 2 && args[0] == "--dump")
{
    var (w, h) = args.Length >= 3 && args[2].Split('x') is [var ws, var hs]
        && int.TryParse(ws, out var pw) && int.TryParse(hs, out var ph)
        ? (pw, ph)
        : (1600, 950);

    DumpFrame(workbench, args[1], w, h);
    return 0;
}

// Headless: `--script <file.json>` drives the workbench through injected input (Guinevere.InputScript).
if (args.Length >= 2 && args[0] == "--script")
{
    var script = InputScript.FromJson(File.ReadAllText(args[1]));
    if (script is null)
    {
        Log.Logger.LogError("Could not read the input script at {Path}", args[1]);
        return 2;
    }

    var scriptInput = new ScriptedInputHandler();
    var player = new InputScriptPlayer(scriptInput, message => Log.Logger.LogInformation("{Message}", message),
        Path.GetDirectoryName(Path.GetFullPath(args[1])));
    var passed = player.Play(script, new Gui { Input = scriptInput }, workbench.Render, WindowFont("Guinevere.font.ttf"));
    return passed ? 0 : 1;
}

Log.Logger.LogInformation("Turian Studio (Gaya) starting");

// The workbench publishes the themed control palette onto the Gui every frame.
var gui = new Gui();
var window = new GuiWindow(gui, 1600, 950, "Turian Studio");
var activeWorkbench = workbench;
try
{
    shell.ExitRequested += window.Close;
    window.RunGui(() => activeWorkbench.Render(gui));
}
finally
{
    shell.ExitRequested -= window.Close;
    window.Dispose();
}

Log.Logger.LogInformation("Studio exited");
return 0;

static void DumpFrame(Workbench workbench, string path, int width, int height)
{
    var gui = new Gui { Input = new HeadlessInput() };
    var font = WindowFont("Guinevere.font.ttf");
    var iconFont = WindowFont("Guinevere.icons.ttf");

    using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Black);

    gui.SetStage(Pass.Pass1Build);
    gui.BeginFrame(canvas, font, iconFont);
    workbench.Render(gui);
    gui.CalculateLayout();
    gui.SetStage(Pass.Pass2Render);
    workbench.Render(gui);
    gui.Render();
    gui.EndFrame();
    canvas.Flush();

    if (Environment.GetEnvironmentVariable("GV_TREE") is not null)
        DumpTree(gui.RootNode!, 0);

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(path);
    data.SaveTo(file);
    Log.Logger.LogInformation("Wrote workbench frame {Width}x{Height} to {Path}", width, height, path);
}

// The fonts the real window embeds, so headless frames draw the same glyphs as the running studio.
static Font WindowFont(string resource) =>
    Font.FromStream(typeof(GuiWindow).Assembly.GetManifestResourceStream(resource)
        ?? throw new InvalidOperationException($"Missing font resource {resource}"));

static void DumpTree(LayoutNode n, int depth)
{
    var r = n.Rect;
    var id = n.Id.Length > 60 ? "…" + n.Id[^58..] : n.Id;
    Console.Error.WriteLine($"{new string(' ', depth * 2)}[{r.X:0},{r.Y:0} {r.W:0}x{r.H:0}] kids={n.Children.Count} {id}");
    foreach (var c in n.Children)
        DumpTree(c, depth + 1);
}
