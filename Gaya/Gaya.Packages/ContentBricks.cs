namespace Gaya.Packages;

/// <summary>What a content-only studio brick provides.</summary>
public enum ContentBrickKind
{
    /// <summary>A color theme (<c>gaya:theme</c>).</summary>
    Theme,

    /// <summary>A look: a complete base sheet shaping every control (<c>gaya:look</c>).</summary>
    Look,

    /// <summary>An icon theme (<c>gaya:icon-theme</c>).</summary>
    IconTheme,

    /// <summary>A font pack (<c>gaya:font-pack</c>).</summary>
    FontPack,
}

/// <summary>
/// Scaffolds content-only studio bricks: a manifest and a <c>Themes/&lt;name&gt;.pss</c> sheet, with no code and no
/// assembly definition. They install with <c>brick add --studio</c> and apply without restarting the studio.
/// </summary>
public static class ContentBricks
{
    /// <summary>The brick category of a kind.</summary>
    /// <param name="kind">The kind.</param>
    /// <returns>The category, such as <c>gaya:theme</c>.</returns>
    public static string Category(ContentBrickKind kind) => kind switch
    {
        ContentBrickKind.Look => "gaya:look",
        ContentBrickKind.IconTheme => "gaya:icon-theme",
        ContentBrickKind.FontPack => "gaya:font-pack",
        _ => "gaya:theme",
    };

    /// <summary>Creates a content brick folder named after <paramref name="id"/> inside <paramref name="parent"/>.</summary>
    /// <param name="parent">The folder the brick folder is created in.</param>
    /// <param name="id">The brick id, lowercase reverse-DNS.</param>
    /// <param name="kind">What the brick provides.</param>
    /// <param name="gaya">The Gaya version the brick requires at least, by major and minor.</param>
    /// <param name="displayName">The name shown to users; derived from the id when null.</param>
    /// <param name="license">The SPDX license expression.</param>
    /// <returns>The brick folder.</returns>
    /// <exception cref="PackageException">The id is invalid or the folder exists.</exception>
    public static string New(string parent, string id, ContentBrickKind kind, SemanticVersion gaya,
        string? displayName = null, string license = "MIT")
    {
        ArgumentNullException.ThrowIfNull(gaya);
        if (!PackageId.IsValid(id))
            throw new PackageException($"'{id}' is not a valid package id (lowercase reverse-DNS, e.g. user.you.nord).");
        var root = Path.Combine(parent, id);
        if (Directory.Exists(root)) throw new PackageException($"{root} already exists.");

        var stem = id.Split('.')[^1];
        var name = displayName ?? char.ToUpperInvariant(stem[0]) + stem[1..].Replace('-', ' ');
        Directory.CreateDirectory(Path.Combine(root, "Themes"));
        new PackageManifest
        {
            Name = id,
            Version = SemanticVersion.Parse("0.1.0"),
            DisplayName = name,
            License = license,
            Scopes = [PackageScope.Studio],
            Engines = { ["gaya"] = VersionRange.Parse($">={gaya.Major}.{gaya.Minor}") },
            Categories = [Category(kind)],
        }.Save(root);

        File.WriteAllText(Path.Combine(root, "Themes", $"{stem}.pss"), Sheet(kind, id, name));
        File.WriteAllText(Path.Combine(root, "README.md"), $"""
            # {name}

            Brick id: `{id}`, a content-only studio brick ({Category(kind)}). Licensed under {license}.

            Edit `Themes/{stem}.pss`, then check it with the studio's `theme verify` command and install it with
            `brick add {id} file:<path to this folder> --studio`.
            """.ReplaceLineEndings("\n") + "\n");
        return root;
    }

    static string Sheet(ContentBrickKind kind, string id, string name) => (kind switch
    {
        ContentBrickKind.Look => $$"""
            @const look-id = "{{id}}";
            @const look-name = "{{name}}";
            @const look-kind = dark;
            @import "guinevere.default";

            // Optional: the color themes applied with this look, by the kind of the current theme.
            // @const look-theme-dark = "gaya.dark";
            // @const look-theme-light = "gaya.light";

            // A look is a complete base sheet: start from Guinevere's default and override shapes, borders and motion.
            // button { border-radius = 0; }
            """,
        ContentBrickKind.IconTheme => $$"""
            @const icon-theme-id = "{{id}}";
            @const icon-theme-name = "{{name}}";
            @const icon-theme-kind = any;

            // icon#asset\.folder { glyph = "\f07b"; }
            """,
        ContentBrickKind.FontPack => $$"""
            @const font-pack-id = "{{id}}";
            @const font-pack-name = "{{name}}";

            // Put the font files beside this sheet:
            // @font-face { font-family = "{{name}}"; src = url("{{name}}-Regular.ttf"); }
            """,
        _ => $"""
            @const theme-id = "{id}";
            @const theme-name = "{name}";
            @const theme-kind = dark;
            @import "gaya.base";

            // Three seeds derive every other token; override any token below them.
            $base = #2e3440;
            $accent = #88c0d0;
            $contrast = 0.3;
            """,
    }).ReplaceLineEndings("\n") + "\n";
}
