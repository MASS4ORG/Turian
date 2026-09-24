namespace Turian.Engine.UI;

/// <summary>A single node in a parsed <c>.ui</c> document tree.</summary>
[PublicAPI]
public sealed class UiElement
{
    /// <summary>Local element name, e.g. <c>Label</c>, <c>Button</c>, <c>VisualElement</c>.</summary>
    public string Tag { get; init; } = "VisualElement";

    /// <summary>XML namespace the element was declared in, or <c>null</c> for the default UI namespace.</summary>
    public string? Namespace { get; init; }

    /// <summary>The <c>name</c> attribute — the element's id within the document, or <c>null</c>.</summary>
    public string? Name { get; init; }

    /// <summary>USS classes from the <c>class</c> attribute.</summary>
    public List<string> Classes { get; init; } = [];

    /// <summary>
    /// Every attribute as written, keyed by name. Includes <c>style</c>, typed attributes and any
    /// unknown ones; excludes <c>name</c>, <c>class</c>, <c>binding-*</c> and recognised events.
    /// </summary>
    public Dictionary<string, string> Attributes { get; init; } = [];

    /// <summary>Declarations parsed from the inline <c>style="prop: val; …"</c> attribute.</summary>
    public Dictionary<string, string> InlineStyle { get; init; } = [];

    /// <summary>Bindings declared inline as <c>binding-&lt;attr&gt;="{ … }"</c>.</summary>
    public List<UiAttributeBinding> AttributeBindings { get; init; } = [];

    /// <summary>Event handlers: event name (e.g. <c>click</c>) → controller method name.</summary>
    public Dictionary<string, string> Events { get; init; } = [];

    /// <summary>Inner text of a leaf element (used as <c>text</c> when no <c>text</c> attribute is set).</summary>
    public string? Text { get; init; }

    /// <summary>Set when this element is a <c>&lt;Repeat&gt;</c>; its <see cref="Children"/> are the row template.</summary>
    public UiRepeat? Repeat { get; init; }

    /// <summary>Set when this element is an <c>&lt;Instance&gt;</c> of a template.</summary>
    public UiInstanceRef? Instance { get; init; }

    /// <summary>Child elements in document order.</summary>
    public List<UiElement> Children { get; init; } = [];

    /// <summary>1-based source line, for diagnostics. 0 when unknown.</summary>
    public int Line { get; init; }

    /// <summary>1-based source column, for diagnostics. 0 when unknown.</summary>
    public int Column { get; init; }

    /// <summary>Depth-first enumeration of this element and its descendants.</summary>
    public IEnumerable<UiElement> DescendantsAndSelf()
    {
        yield return this;
        foreach (var child in Children)
            foreach (var d in child.DescendantsAndSelf())
                yield return d;
    }

    /// <summary>Finds the first descendant (or self) with the given <c>name</c>.</summary>
    /// <param name="name">The element name to search for.</param>
    public UiElement? Find(string name) =>
        DescendantsAndSelf().FirstOrDefault(e => e.Name == name);
}

/// <summary>An inline <c>binding-&lt;attr&gt;</c> on an element.</summary>
/// <param name="TargetAttribute">The attribute the binding drives, e.g. <c>text</c>.</param>
/// <param name="Expression">The parsed <c>{ … }</c>.</param>
public sealed record UiAttributeBinding(string TargetAttribute, BindingExpression Expression);

/// <summary>A <c>&lt;Repeat items="{…}" as="x"&gt;</c> loop.</summary>
/// <param name="ItemsPath">Data-context path to the collection.</param>
/// <param name="ItemAlias">Name the current item is bound to inside the loop body (default <c>item</c>).</param>
public sealed record UiRepeat(string ItemsPath, string ItemAlias = "item");

/// <summary>A reference to a named <c>&lt;Template&gt;</c> from an <c>&lt;Instance&gt;</c>.</summary>
/// <param name="TemplateName">The template's <c>name</c>.</param>
/// <param name="Parameters">Attribute values passed to the template, substituted for <c>{param}</c> placeholders.</param>
public sealed record UiInstanceRef(string TemplateName, Dictionary<string, string> Parameters);
