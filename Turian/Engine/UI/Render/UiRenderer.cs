namespace Turian.Engine.UI;

/// <summary>
/// Walks a <see cref="UiDocument"/> each frame and emits the matching Guinevere primitives and
/// controls. Plug <see cref="Render"/> into a <c>UiDocumentComponent.OnBuild</c> or a
/// <c>UiRuntime</c>.
/// </summary>
/// <remarks>
/// Inline <c>style</c> declarations are applied via <see cref="UiStyleApplier"/>. Class-based
/// <c>.uss</c> resolution and the full data-binding engine are separate steps; this renderer uses
/// literal attribute values plus <see cref="DataResolver"/> for any bindings and
/// <see cref="ImageResolver"/> for image sources.
/// </remarks>
public sealed partial class UiRenderer
{
    readonly UiDocument document;
    readonly Dictionary<string, object> state = new(StringComparer.Ordinal);
    readonly Dictionary<string, UiElement> expandedTemplates = new(StringComparer.Ordinal);
    readonly Stack<(string Alias, object? Item)> repeatScope = new();

    int autoKey;

    /// <summary>Creates a renderer for a document.</summary>
    /// <param name="document">The parsed document to render.</param>
    public UiRenderer(UiDocument document) => this.document = document ?? throw new ArgumentNullException(nameof(document));

    /// <summary>Event handlers by method name, invoked when a matching element event fires.</summary>
    public IDictionary<string, Action> Handlers { get; } = new Dictionary<string, Action>(StringComparer.Ordinal);

    /// <summary>Resolves an image <c>src</c> (path or <c>asset://</c>) to an image, or <c>null</c>.</summary>
    public Func<string, SKImage?>? ImageResolver { get; set; }

    /// <summary>Resolves a font reference (path, <c>asset://</c> or family name) to a base font, or <c>null</c>.</summary>
    public Func<string, Font?>? FontResolver { get; set; }

    /// <summary>Resolves a binding path to a value. Set directly, or via <see cref="Bind"/>.</summary>
    public Func<string, object?>? DataResolver { get; set; }

    /// <summary>
    /// Localizes an element's text: <c>(key, source)</c> where <c>key</c> is an explicit
    /// <c>text-key</c> attribute or <c>null</c>, and <c>source</c> is the authored text
    /// (or bound value) that doubles as the lookup key when no explicit key names one. Called for
    /// labels, button text, toggles, tab headers and field placeholders.
    /// </summary>
    public Func<string?, string?, string>? TextResolver { get; set; }

    /// <summary>
    /// <c>.uss</c> stylesheets to apply, lowest priority first. Populated from the document's
    /// <c>&lt;Style&gt;</c> references by whoever loads it.
    /// </summary>
    public IList<StyleSheet> StyleSheets { get; } = new List<StyleSheet>();

    BindingContext? bindingContext;
    bool bindingsFolded;

    /// <summary>
    /// Wires a data context and/or a <see cref="UiController"/>: bindings resolve against
    /// <paramref name="dataContext"/> (defaults to <paramref name="controller"/>), events call the
    /// controller's public parameterless methods, and any <c>&lt;Bindings&gt;</c> entries are folded
    /// onto their target elements.
    /// </summary>
    /// <param name="dataContext">Object binding paths resolve against, or <c>null</c> to use the controller.</param>
    /// <param name="controller">Code-behind whose public methods become event handlers.</param>
    public void Bind(object? dataContext = null, UiController? controller = null)
    {
        if (controller is not null)
        {
            controller.Attach(document);
            foreach (var (name, action) in controller.DiscoverHandlers())
                Handlers[name] = action;
        }

        bindingContext = new BindingContext(dataContext ?? controller);
        DataResolver = bindingContext.Resolve;

        FoldExplicitBindings();
    }

    void FoldExplicitBindings()
    {
        if (bindingsFolded) return;
        bindingsFolded = true;

        foreach (var b in document.Bindings)
        {
            var target = document.Find(b.Element);
            if (target is null) continue;
            if (target.AttributeBindings.Any(x => x.TargetAttribute == b.Property)) continue;
            target.AttributeBindings.Add(
                new UiAttributeBinding(b.Property, new BindingExpression(b.Path, b.Mode, b.Converter)));
        }
    }

    /// <summary>Renders one frame of the document into <paramref name="gui"/>.</summary>
    /// <param name="gui">The Guinevere context for the current pass.</param>
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);
        autoKey = 0;

        gui.StyleSheets.Clear();
        foreach (var sheet in StyleSheets)
            gui.StyleSheets.Add(sheet);

        RenderElement(gui, document.Root);
    }

    void RenderElement(Gui gui, UiElement el)
    {
        if (el.Instance is { } instance)
        {
            RenderInstance(gui, instance);
            return;
        }

        if (el.Repeat is { } repeat)
        {
            RenderRepeat(gui, el, repeat);
            return;
        }

        switch (el.Tag)
        {
            case "Label": RenderLabel(gui, el); break;
            case "Button": RenderButton(gui, el); break;
            case "Image": RenderImage(gui, el); break;
            case "ImageButton": RenderImageButton(gui, el); break;
            case "TextField": RenderTextField(gui, el); break;
            case "Toggle": RenderToggle(gui, el); break;
            case "ScrollView": RenderScrollView(gui, el); break;
            case "Tabs": RenderTabs(gui, el); break;
            default: RenderContainer(gui, el); break;
        }
    }

    // ── containers ────────────────────────────────────────────────────────────

    void RenderContainer(Gui gui, UiElement el)
    {
        var node = gui.StyledNode(el.Tag, el.Classes, el.Name);
        UiStyleApplier.Apply(node, el.InlineStyle);
        using (node.Enter())
        {
            LogRect(gui, el, node);
            DrawBackground(gui, el);
            foreach (var child in el.Children)
                RenderElement(gui, child);
        }
    }

    // Diagnostic: TURIAN_UI_LAYOUT=1 logs each container's computed rect in the render pass, so a
    // "UI zoomed then vanished" report can be traced to the actual coordinates.
    static bool LayoutDiag => Environment.GetEnvironmentVariable("TURIAN_UI_LAYOUT") == "1";

    static void LogRect(Gui gui, UiElement el, LayoutNode node)
    {
        if (!LayoutDiag || gui.Pass != Pass.Pass2Render) return;
        var r = node.Rect;
        var id = el.Name is { Length: > 0 } n ? $"#{n}" : el.Tag;
        var cls = el.Classes.Count > 0 ? "." + string.Join('.', el.Classes) : string.Empty;
        Log.Logger.LogInformation(
            "UiLayout: {Id}{Cls} rect=({X:0},{Y:0} {W:0}x{H:0}) screen={SW:0}x{SH:0}",
            id, cls, r.X, r.Y, r.W, r.H, gui.ScreenRect.W, gui.ScreenRect.H);
    }

    void RenderScrollView(Gui gui, UiElement el)
    {
        var node = gui.StyledNode(el.Tag, el.Classes, el.Name);
        UiStyleApplier.Apply(node, el.InlineStyle);
        using (node.Enter())
        {
            LogRect(gui, el, node);
            gui.ScrollY();
            gui.ClipContent();
            DrawBackground(gui, el);
            foreach (var child in el.Children)
                RenderElement(gui, child);
        }
    }

    /// <summary>
    /// A header strip of <c>Tab</c> elements over the active tab's children. The selected header
    /// carries a <c>selected</c> class, so a sheet styles the two states with
    /// <c>Tab</c> and <c>Tab.selected</c>.
    /// </summary>
    void RenderTabs(Gui gui, UiElement el)
    {
        var key = StateKey(el, "tab");
        var active = state.TryGetValue(key, out var v) && v is int i ? i : 0;

        var tabs = el.Children.Where(child => child.Tag == "Tab").ToList();
        if (tabs.Count == 0) return;

        active = Math.Clamp(active, 0, tabs.Count - 1);
        var next = active;

        var node = gui.StyledNode(el.Tag, el.Classes, el.Name);
        ApplyAttributeSize(node, el);
        UiStyleApplier.Apply(node, el.InlineStyle);

        using (node.Enter())
        {
            LogRect(gui, el, node);
            DrawBackground(gui, el);

            using (gui.StyledNode("TabStrip", el.Classes).Direction(Axis.Horizontal).Enter())
                for (var t = 0; t < tabs.Count; t++)
                    if (RenderTabHeader(gui, tabs[t], t, t == active))
                        next = t;

            foreach (var child in tabs[active].Children)
                RenderElement(gui, child);
        }

        if (next != active) Fire(el, "changed");
        state[key] = next;
    }

    bool RenderTabHeader(Gui gui, UiElement tab, int index, bool selected)
    {
        var classes = WithClass(tab.Classes, "selected", selected);
        var node = gui.StyledNode(tab.Tag, classes, tab.Name);
        ApplyAttributeSize(node, tab);
        UiStyleApplier.Apply(node, tab.InlineStyle);

        using (node.Enter())
        {
            var clicked = gui.Pass == Pass.Pass2Render && gui.GetInteractable().OnClick();
            DrawLabel(gui, tab, ResolveTextAttribute(tab, "header") ?? $"Tab {index + 1}");
            return clicked;
        }
    }

    /// <summary>
    /// Honours <c>width</c> / <c>height</c> written as attributes rather than as style declarations,
    /// which is how a control tag in a <c>.ui</c> document is normally sized. An inline style still
    /// wins, because <see cref="UiStyleApplier"/> runs after this.
    /// </summary>
    static void ApplyAttributeSize(LayoutNode node, UiElement el)
    {
        if (UiValue.TryLength(Attr(el, "width") ?? string.Empty, out var w, out _) && w > 0f) node.Width(w);
        if (UiValue.TryLength(Attr(el, "height") ?? string.Empty, out var h, out _) && h > 0f) node.Height(h);
    }

    /// <summary>The element's classes plus a state class, so a sheet can target the state.</summary>
    static IReadOnlyList<string> WithClass(IReadOnlyList<string> classes, string name, bool present)
    {
        if (!present) return classes;

        var combined = new List<string>(classes.Count + 1);
        combined.AddRange(classes);
        combined.Add(name);
        return combined;
    }

    // ── leaves ───────────────────────────────────────────────────────────────

}
