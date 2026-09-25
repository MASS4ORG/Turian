namespace Turian.Engine.UI;

/// <summary>
/// Parses the <c>.ui</c> XML authoring format into a <see cref="UiDocument"/>. Reports the source
/// location of the first structural error via <see cref="UiParseException"/>.
/// </summary>
public static class UiXmlParser
{
    /// <summary>The default XML namespace for the UI vocabulary.</summary>
    public const string Namespace = "https://turian.mass4.org/ui";

    static readonly HashSet<string> eventAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "click", "value-changed", "changed", "submit", "activated", "selection-changed",
    };

    /// <summary>Parses <paramref name="xml"/> into a document.</summary>
    /// <param name="xml">The <c>.ui</c> document text.</param>
    /// <param name="sourcePath">Optional path recorded on the document for diagnostics.</param>
    /// <exception cref="UiParseException">The document is not well-formed <c>.ui</c> XML.</exception>
    public static UiDocument Parse(string xml, string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);

        XDocument xdoc;
        try
        {
            xdoc = XDocument.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new UiParseException(ex.Message.Split('.')[0], ex.LineNumber, ex.LinePosition);
        }

        var root = xdoc.Root ?? throw new UiParseException("The document is empty", 1, 1);
        if (root.Name.LocalName != "UI")
            throw Fail(root, $"Root element must be <UI>, found <{root.Name.LocalName}>");

        var styleSheets = new List<string>();
        var templates = new Dictionary<string, UiTemplate>();
        var bindings = new List<UiBinding>();
        UiElement? visualRoot = null;
        var strayRoots = new List<UiElement>();

        foreach (var child in root.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "Style":
                    styleSheets.Add(RequireAttr(child, "src"));
                    break;

                case "Template":
                    {
                        var name = RequireAttr(child, "name");
                        var body = child.Elements().SingleOrDefault()
                                   ?? throw Fail(child, "<Template> must contain exactly one root element");
                        templates[name] = new UiTemplate(
                            name, ParseElement(body, bindings), body.ToString(SaveOptions.DisableFormatting), LineOf(child));
                        break;
                    }

                case "Bindings":
                    CollectBindings(child, bindings);
                    break;

                default:
                    if (visualRoot is null) visualRoot = ParseElement(child, bindings);
                    else strayRoots.Add(ParseElement(child, bindings));
                    break;
            }
        }

        if (visualRoot is null)
            throw Fail(root, "<UI> must contain a visual element");

        if (strayRoots.Count > 0)
        {
            var wrapper = new UiElement { Tag = "VisualElement", Line = visualRoot.Line };
            wrapper.Children.Add(visualRoot);
            wrapper.Children.AddRange(strayRoots);
            visualRoot = wrapper;
        }

        return new UiDocument
        {
            ControllerType = root.Attribute("controller")?.Value,
            StyleSheets = styleSheets,
            Templates = templates,
            Root = visualRoot,
            Bindings = bindings,
            SourcePath = sourcePath,
        };
    }

    /// <summary>
    /// Parses a single element fragment (one XML element, with its own namespace declaration) —
    /// used to expand a <c>&lt;Template&gt;</c> after <c>$param</c> substitution.
    /// </summary>
    /// <param name="xml">One well-formed XML element.</param>
    /// <exception cref="UiParseException">The fragment is not well-formed.</exception>
    public static UiElement ParseFragment(string xml)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(xml);
        XElement el;
        try
        {
            el = XElement.Parse(xml, LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new UiParseException(ex.Message.Split('.')[0], ex.LineNumber, ex.LinePosition);
        }

        return ParseElement(el, []);
    }

    static void CollectBindings(XElement bindingsElement, List<UiBinding> into)
    {
        foreach (var b in bindingsElement.Elements().Where(e => e.Name.LocalName == "Binding"))
            into.Add(ParseBinding(b));
    }

    static UiElement ParseElement(XElement el, List<UiBinding> bindings)
    {
        var tag = el.Name.LocalName;
        var ns = el.Name.NamespaceName;

        UiRepeat? repeat = null;
        UiInstanceRef? instance = null;

        if (tag == "Repeat")
        {
            var items = RequireAttr(el, "items");
            if (!BindingExpression.IsBinding(items))
                throw Fail(el, "<Repeat items> must be a binding expression, e.g. items=\"{Path.To.List}\"");
            repeat = new UiRepeat(BindingExpression.Parse(items).Path, el.Attribute("as")?.Value ?? "item");
        }
        else if (tag == "Instance")
        {
            var template = RequireAttr(el, "template");
            var parameters = el.Attributes()
                .Where(a => a.Name.LocalName is not ("template" or "name") && !a.IsNamespaceDeclaration)
                .ToDictionary(a => a.Name.LocalName, a => a.Value, StringComparer.Ordinal);
            instance = new UiInstanceRef(template, parameters);
        }

        var childElements = el.Elements().ToList();
        var innerText = childElements.Count == 0 ? el.Value.Trim() : string.Empty;

        var element = new UiElement
        {
            Tag = tag,
            Namespace = string.IsNullOrEmpty(ns) || ns == Namespace ? null : ns,
            Name = el.Attribute("name")?.Value,
            Text = innerText.Length > 0 ? innerText : null,
            Repeat = repeat,
            Instance = instance,
            Line = LineOf(el),
            Column = ColumnOf(el),
        };

        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            var an = attr.Name.LocalName;
            var av = attr.Value;

            switch (an)
            {
                case "name":
                    continue;
                case "class":
                    element.Classes.AddRange(av.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                    continue;
                case "style":
                    foreach (var decl in av.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var colon = decl.IndexOf(':', StringComparison.Ordinal);
                        if (colon > 0)
                            element.InlineStyle[decl[..colon].Trim()] = decl[(colon + 1)..].Trim();
                    }

                    continue;
            }

            if (tag == "Instance" && an is "template") continue;
            if (tag == "Repeat" && an is "items" or "as") continue;

            if (an.StartsWith("binding-", StringComparison.Ordinal))
            {
                var target = an["binding-".Length..];
                element.AttributeBindings.Add(new UiAttributeBinding(target, ParseBindingExpr(el, av)));
                continue;
            }

            if (eventAttributes.Contains(an))
            {
                // A method name, or {controller.Method} — strip the braces either way.
                element.Events[an] = BindingExpression.IsBinding(av) ? av[1..^1].Trim() : av;
                continue;
            }

            if (BindingExpression.IsBinding(av))
            {
                // A plain attribute whose value is a { … } expression is an implicit one-way binding.
                element.AttributeBindings.Add(new UiAttributeBinding(an, ParseBindingExpr(el, av)));
                continue;
            }

            element.Attributes[an] = av;
        }

        foreach (var childEl in childElements)
        {
            if (childEl.Name.LocalName == "Bindings")
            {
                CollectBindings(childEl, bindings);
                continue;
            }

            element.Children.Add(ParseElement(childEl, bindings));
        }

        return element;
    }

    static UiBinding ParseBinding(XElement b)
    {
        var mode = BindingMode.OneWay;
        var modeAttr = b.Attribute("mode")?.Value;
        if (modeAttr is not null && Enum.TryParse<BindingMode>(modeAttr, ignoreCase: true, out var parsedMode))
            mode = parsedMode;

        return new UiBinding(
            RequireAttr(b, "element"),
            RequireAttr(b, "property"),
            RequireAttr(b, "path"),
            mode,
            b.Attribute("converter")?.Value);
    }

    static BindingExpression ParseBindingExpr(XElement el, string value)
    {
        try
        {
            return BindingExpression.Parse(value);
        }
        catch (FormatException ex)
        {
            throw Fail(el, ex.Message);
        }
    }

    static string RequireAttr(XElement el, string name) =>
        el.Attribute(name)?.Value
        ?? throw Fail(el, $"<{el.Name.LocalName}> requires a '{name}' attribute");

    static int LineOf(XElement el) => el is IXmlLineInfo li && li.HasLineInfo() ? li.LineNumber : 0;

    static int ColumnOf(XElement el) => el is IXmlLineInfo li && li.HasLineInfo() ? li.LinePosition : 0;

    static UiParseException Fail(XElement el, string message) =>
        new(message, LineOf(el), ColumnOf(el));
}
