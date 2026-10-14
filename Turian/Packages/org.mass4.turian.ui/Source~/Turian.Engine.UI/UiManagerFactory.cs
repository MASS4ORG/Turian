[assembly: UiPresenterFactory(typeof(Turian.Engine.UI.UiManagerFactory))]

namespace Turian.Engine.UI;

/// <summary>Gives hosts a <see cref="UiManager"/> without naming it, and renders single documents for previews.</summary>
public sealed class UiManagerFactory : IUiPresenterFactory, IUiDocumentPreview
{
    /// <inheritdoc/>
    public IUiPresenter Create(Vulkan vulkan, IInputSource? input, LocaleService? locale) =>
        new UiManager(vulkan, input, locale);

    /// <inheritdoc/>
    public byte[] RenderPng(string documentPath, int width, int height, string? dataJson)
    {
        var path = Path.GetFullPath(documentPath);
        var document = UiXmlParser.Parse(File.ReadAllText(path), path);
        var baseDir = Path.GetDirectoryName(path)!;

        var assetsRoot = FindAssetsRoot(baseDir);

        var renderer = new UiRenderer(document)
        {
            ImageResolver = new UiImageResolver(null, baseDir, assetsRoot).Resolve,
            FontResolver = new UiFontResolver(null, baseDir, assetsRoot).Resolve,
        };
        foreach (var source in document.StyleSheets)
        {
            var stylesheet = Path.GetFullPath(Path.Combine(baseDir, source));
            if (File.Exists(stylesheet))
            {
                renderer.StyleSheets.Add(StyleSheet.Parse(File.ReadAllText(stylesheet), UiStyleSheetAsset.ParseOptions));
            }
            else
            {
                Log.Logger.LogWarning("Stylesheet not found: {Path}", stylesheet);
            }
        }

        if (!string.IsNullOrEmpty(dataJson))
        {
            renderer.Bind(ToDictionary(dataJson));
        }

        return UiImageRenderer.RenderPng(renderer.Render, width, height, background: new SKColor(12, 14, 20));
    }

    static string FindAssetsRoot(string baseDir)
    {
        for (var directory = new DirectoryInfo(baseDir); directory is not null; directory = directory.Parent)
        {
            if (string.Equals(directory.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return directory.Parent?.FullName ?? baseDir;
            }
        }

        return baseDir;
    }

    /// <summary>Parses a JSON object into nested dictionaries and lists for binding.</summary>
    static Dictionary<string, object?> ToDictionary(string json)
    {
        using var document = JsonDocument.Parse(json);
        return (Dictionary<string, object?>)Convert(document.RootElement)!;

        static object? Convert(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().ToDictionary(static p => p.Name, static p => Convert(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Select(Convert).ToList(),
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var integer) ? integer : element.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }
}
