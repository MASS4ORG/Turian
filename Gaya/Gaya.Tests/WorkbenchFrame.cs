namespace Gaya.Tests;

/// <summary>
/// A headless Gaya workbench with isolated settings and layout files, drawn through both GUI passes. Also compiled
/// into the engine test project so its chrome tests share the same harness.
/// </summary>
sealed class WorkbenchFrame : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), $"gaya-chrome-{Guid.NewGuid():N}");
    readonly SKSurface surface = SKSurface.Create(new SKImageInfo(800, 600));
    readonly Font font = Font.FromFamilyName("sans-serif", 14);
    readonly GayaApplication application;

    /// <summary>Creates an isolated workbench with temporary settings and layout files.</summary>
    public WorkbenchFrame(float? zoom = null, IWindowIdentity? identity = null)
    {
        Directory.CreateDirectory(directory);
        var settings = new EditorSettings(NullLogger.Instance, Path.Combine(directory, "settings.json"));
        var themes = new ThemeService();
        var registrations = new ServiceCollection().AddSingleton<ILogger>(NullLogger.Instance)
            .AddSingleton<IEditorSettings>(settings).AddSingleton<IThemeService>(themes)
            .AddSingleton(Blocker);
        if (identity is not null) registrations.AddSingleton(identity);
        var services = registrations.BuildServiceProvider();
        application = new GayaApplication(services, new PanelRegistry(), new CommandRegistry(),
            new MenuRegistry(), Chrome, new ShortcutService(NullLogger.Instance),
            new FocusTracker(), [], settings: settings, themes: themes);
        Workbench = new Workbench(application, null,
            new WorkbenchLayoutStore(NullLogger.Instance, Path.Combine(directory, "layout.json")));
        if (zoom is { } scale) application.Themes.SetScale(ThemeTokens.Default.FontSize, scale);
        Input.MousePosition.Returns(new Vector2(-1, -1));
        Input.PrevMousePosition.Returns(new Vector2(-1, -1));
        Gui.Input = Input;
    }

    /// <summary>The contributions rendered by the workbench.</summary>
    public ChromeRegistry Chrome { get; } = new();
    /// <summary>The input state supplied to both passes.</summary>
    public IInputHandler Input { get; } = Substitute.For<IInputHandler>();
    /// <summary>The persistent GUI context for the headless frames.</summary>
    public Gui Gui { get; } = new();
    /// <summary>The shell under test.</summary>
    public Workbench Workbench { get; }
    /// <summary>The service controlling the modal input blocker.</summary>
    public IUiBlocker Blocker { get; } = Substitute.For<IUiBlocker>();
    /// <summary>The commands available to menus and the command palette.</summary>
    public CommandRegistry Commands => application.Commands;
    /// <summary>The registered menu entries.</summary>
    public MenuRegistry Menus => application.Menus;
    /// <summary>The page registry and persistence used by the workbench.</summary>
    public EditorSettings Settings => application.Settings;
    /// <summary>The services used by contributed panels.</summary>
    public IServiceProvider Services => application.Services;
    /// <summary>The themes available to menus and the Appearance settings page.</summary>
    public ThemeService Themes => application.Themes;
    /// <summary>The panels rendered by the workbench.</summary>
    public PanelRegistry Panels => application.Panels;

    /// <summary>Draws both passes, optionally mutating contributions after layout is calculated.</summary>
    public void Draw(Action? betweenPasses = null)
    {
        Gui.Time.Update(0.016);
        Gui.SetStage(Pass.Pass1Build);
        Gui.BeginFrame(surface.Canvas, font, font);
        Workbench.Render(Gui);
        Gui.CalculateLayout();
        betweenPasses?.Invoke();
        Gui.SetStage(Pass.Pass2Render);
        Workbench.Render(Gui);
        Gui.Render();
        Gui.EndFrame();
    }

    /// <summary>Writes the workbench's offscreen surface for visual inspection.</summary>
    public void Capture(string path)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    /// <summary>Releases the host and drawing surface and deletes its temporary files.</summary>
    public void Dispose()
    {
        Workbench.Dispose();
        application.Dispose();
        surface.Dispose();
        Directory.Delete(directory, true);
    }

    /// <summary>Runs one complete two-pass frame of a standalone <paramref name="render"/> callback.</summary>
    public static void Render(Gui gui, SKSurface surface, Font font, Action<Gui> render)
    {
        gui.SetStage(Pass.Pass1Build);
        gui.BeginFrame(surface.Canvas, font, font);
        render(gui);
        gui.CalculateLayout();
        gui.SetStage(Pass.Pass2Render);
        render(gui);
        gui.Render();
        gui.EndFrame();
    }

    /// <summary>Enumerates <paramref name="node"/> and every node beneath it, depth first.</summary>
    public static IEnumerable<LayoutNode> Descendants(LayoutNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Descendants(child)) yield return descendant;
    }
}
