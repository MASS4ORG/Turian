namespace Gaya.Plugin.Turian;

/// <summary>
/// The versions a registry lists of a brick, as the inspector offers them: newest first, each marked when it is in
/// use, downloaded, deprecated or withdrawn, with the one the user picked.
/// </summary>
sealed class BrickVersionChoice
{
    /// <summary>Builds the choice for a brick, starting on the version in use, else the newest.</summary>
    /// <param name="brick">The brick.</param>
    public BrickVersionChoice(CatalogBrick brick)
    {
        Versions = [.. brick.Versions.Select(static v => v.Version)];
        Labels = [.. brick.Versions.Select(v => Label(brick, v))];
        Selected = Math.Max(0, Array.IndexOf(Versions, brick.IsInstalled ? brick.InstalledVersion : null));
    }

    /// <summary>The versions, newest first.</summary>
    public string[] Versions { get; }

    /// <summary>What the dropdown shows for each version.</summary>
    public string[] Labels { get; }

    /// <summary>The index of the picked version.</summary>
    public int Selected { get; set; }

    /// <summary>The picked version, or null when there is none.</summary>
    public string? Picked => Selected >= 0 && Selected < Versions.Length ? Versions[Selected] : null;

    static string Label(CatalogBrick brick, ListedVersion version)
    {
        var marks = new List<string>();
        if (brick.IsInstalled && brick.InstalledVersion == version.Version)
        {
            marks.Add("in use");
        }
        else if (brick.Downloads.Any(d => d.Version == version.Version))
        {
            marks.Add("downloaded");
        }

        if (version.Yanked)
        {
            marks.Add("withdrawn");
        }
        else if (version.Deprecation is not null)
        {
            marks.Add("deprecated");
        }

        return marks.Count == 0 ? version.Version : $"{version.Version} · {string.Join(", ", marks)}";
    }
}

/// <summary>Draws a <see cref="BrickVersionChoice"/> as a labelled dropdown.</summary>
sealed class BrickVersionDrawer : IPropertyDrawer
{
    /// <summary>The shared drawer.</summary>
    public static readonly BrickVersionDrawer Instance = new();

    /// <summary>Whether the drawer takes a field: a brick's version choice.</summary>
    /// <param name="field">The field.</param>
    /// <returns>True for a <see cref="BrickVersionChoice"/>.</returns>
    public static bool Takes(FormField field) => field.ValueType == typeof(BrickVersionChoice);

    /// <inheritdoc />
    public void Draw(Gui gui, FormField field, string id, FormRenderContext context) =>
        FormControls.Row(gui, field.Label, id, () => DrawValue(gui, field, id, context));

    /// <inheritdoc />
    public bool DrawValue(Gui gui, FormField field, string id, FormRenderContext context)
    {
        if (field.GetValue() is not BrickVersionChoice choice || choice.Versions.Length == 0)
        {
            return false;
        }

        var theme = ThemeTokens.Current;
        var selected = choice.Selected;
        gui.Dropdown(choice.Labels, ref selected, width: 0, height: theme.Scale(theme.RowHeight), placeholder: "Version",
            fontSize: theme.Text(12), maxVisibleItems: 10, filePath: $"{id}/versions");
        choice.Selected = selected;
        return true;
    }
}
