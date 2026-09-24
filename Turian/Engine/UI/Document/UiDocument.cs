namespace Turian.Engine.UI;

/// <summary>
/// A parsed <c>.ui</c> document: an ordered list of stylesheet references, named templates, the
/// visual-element tree, and any explicit <c>&lt;Bindings&gt;</c>. Authored as XML; round-trips to
/// JSON for tooling and for the baked import artifact.
/// </summary>
public sealed class UiDocument
{
    /// <summary>Fully-qualified <c>UiController</c> type from the root <c>controller</c> attribute, or <c>null</c>.</summary>
    public string? ControllerType { get; init; }

    /// <summary><c>src</c> of each <c>&lt;Style&gt;</c>, in the order they should be applied.</summary>
    public List<string> StyleSheets { get; init; } = [];

    /// <summary>Named <c>&lt;Template&gt;</c> sub-trees, keyed by name.</summary>
    public Dictionary<string, UiTemplate> Templates { get; init; } = [];

    /// <summary>The root visual element (the single child of <c>&lt;UI&gt;</c>).</summary>
    public UiElement Root { get; init; } = new();

    /// <summary>Bindings collected from every <c>&lt;Bindings&gt;</c> block, flattened.</summary>
    public List<UiBinding> Bindings { get; init; } = [];

    /// <summary>Project-relative path the document was loaded from, for diagnostics. Optional.</summary>
    public string? SourcePath { get; init; }

    static readonly JsonSerializerOptions jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Serializes the document to JSON (used by tooling and the baked import artifact).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, jsonOptions);

    /// <summary>Rebuilds a document from JSON produced by <see cref="ToJson"/>.</summary>
    /// <param name="json">JSON text.</param>
    public static UiDocument FromJson(string json) =>
        JsonSerializer.Deserialize<UiDocument>(json, jsonOptions)
        ?? throw new JsonException("The JSON did not contain a UI document");

    /// <summary>Finds the first element in the tree with the given <c>name</c>.</summary>
    /// <param name="name">The element name.</param>
    public UiElement? Find(string name) => Root.Find(name);

    /// <summary>Every element in the tree, depth-first.</summary>
    public IEnumerable<UiElement> Elements() => Root.DescendantsAndSelf();
}

/// <summary>A named, reusable sub-tree declared with <c>&lt;Template&gt;</c>.</summary>
/// <param name="Name">The template's name, referenced by <c>&lt;Instance template="…"&gt;</c>.</param>
/// <param name="Root">The template's single root element (with <c>$param</c> placeholders unsubstituted).</param>
/// <param name="RawXml">The template body as XML, for <c>$param</c> substitution at instance time.</param>
/// <param name="Line">Source line of the declaration.</param>
public sealed record UiTemplate(string Name, UiElement Root, string RawXml = "", int Line = 0);

/// <summary>An entry from a <c>&lt;Bindings&gt;</c> block.</summary>
/// <param name="Element">Target element <c>name</c>.</param>
/// <param name="Property">Target property, e.g. <c>text</c> or <c>enabled</c>.</param>
/// <param name="Path">Data-context path.</param>
/// <param name="Mode">Binding direction.</param>
/// <param name="Converter">Optional named converter.</param>
public sealed record UiBinding(
    string Element,
    string Property,
    string Path,
    BindingMode Mode = BindingMode.OneWay,
    string? Converter = null);
