namespace Gaya.Plugin.Turian;

/// <summary>
/// Hosts the Help ▸ About dialog: the studio's logo, name, build version and compilation date, links
/// to documentation and community spaces, and the contributor list from <c>docs/CONTRIBUTORS.md</c>.
/// Draws nothing of its own in the menu bar strip it is registered on — it only needs a slot that
/// renders every frame, so <see cref="Open"/> (called from the Help menu's command) can flip the
/// dialog open on the next one. The version and contributors come from <see cref="BuildInfo"/>,
/// generated at compile time by <c>BuildInfoGenerator</c>.
/// </summary>
sealed class AboutDialogChrome : IChromeItem
{
    const float dialogWidth = 360f;
    const float bodyHeight = 420f;
    const float footerHeight = 44f;
    const float logoSize = 96f;

    /// <summary>The dialog body's own padding (16 either side) subtracted from <see cref="dialogWidth"/>.</summary>
    const float contentWidth = dialogWidth - 32f;

    static readonly Lazy<SKImage?> logo = new(LoadLogo);

    static StudioTheme Theme => StudioTheme.Current;

    bool isOpen;

    /// <summary>Opens the dialog. Called from the Help ▸ About command.</summary>
    public void Open() => isOpen = true;

    /// <inheritdoc />
    public void Render(Gui gui)
    {
        ArgumentNullException.ThrowIfNull(gui);

        gui.Dialog(ref isOpen, "About Turian Studio", () => Body(gui),
            Theme.Scale(dialogWidth), Theme.Scale(bodyHeight), () => Footer(gui),
            footerHeight: Theme.Scale(footerHeight));
    }

    static void Body(Gui gui)
    {
        using (gui.Node().Expand().Direction(Axis.Vertical).ContentAlignX(0.5f).Gap(Theme.Scale(12f)).Enter())
        {
            gui.ScrollY();
            Header(gui);
            Links(gui);
            Contributors(gui);
        }
    }

    static void Header(Gui gui)
    {
        using (gui.Node().ExpandWidth().Direction(Axis.Vertical).ContentAlignX(0.5f)
                   .Gap(Theme.Scale(4f)).Enter())
        {
            if (logo.Value is { } image) gui.Image(image, Theme.Scale(logoSize), Theme.Scale(logoSize));
            else gui.DrawText("◉", Theme.Text(52f), Theme.Accent);

            gui.DrawText("Turian Studio", Theme.Text(18f), Theme.Ink);
            gui.DrawText($"Version {BuildInfo.Version}", Theme.Text(12f), Theme.InkDim);
            gui.DrawText($"Built {BuildInfo.CompilationDate}", Theme.Text(11f), Theme.InkDim);
        }
    }

    static void Links(Gui gui)
    {
        using (gui.Node().ExpandWidth().Direction(Axis.Vertical).Gap(Theme.Scale(8f)).Enter())
        {
            Divider(gui);
            gui.DrawText("Documentation & Community", Theme.Text(14f), Theme.Ink, centerInRect: false);

            Link(gui, "Documentation", "https://Turian.MASS4.org/");
            Link(gui, "Blog", "https://Turian.MASS4.org/blog");
            Link(gui, "Changelog", "https://github.com/MASS4ORG/Turian/blob/main/CHANGELOG.md");
            Link(gui, "Discord", "https://discord.com/channels/1104509879269457982/1104509879865057342");
            Link(gui, "Matrix", "https://matrix.to/#/!yyiCIgJgQDezXOaEgK:matrix.org?via=matrix.org");
            Link(gui, "Issues", "https://github.com/MASS4ORG/Turian/issues");
        }
    }

    static void Contributors(Gui gui)
    {
        using (gui.Node().ExpandWidth().Direction(Axis.Vertical).Gap(Theme.Scale(4f)).Enter())
        {
            Divider(gui);
            gui.DrawText("Contributors", Theme.Text(14f), Theme.Ink, centerInRect: false);

            foreach (var line in BuildInfo.Contributors.Split('\n'))
            {
                if (line.Length == 0) continue;
                gui.DrawText(line, Theme.Text(12f), Theme.InkDim, centerInRect: false);
            }
        }
    }

    static void Divider(Gui gui)
    {
        using (gui.Node(Theme.Scale(contentWidth), Theme.Scale(1f)).Enter())
            gui.DrawBackgroundRect(Theme.Border);
    }

    static void Link(Gui gui, string label, string url)
    {
        if (gui.Button(label, width: Theme.Scale(contentWidth), height: Theme.Scale(28f)))
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
    }

    void Footer(Gui gui)
    {
        if (gui.Button("Close", width: Theme.Scale(80f), height: Theme.Scale(28f))) isOpen = false;
    }

    /// <summary>Loads the embedded logo once; falls back to a glyph when the resource is missing.</summary>
    static SKImage? LoadLogo()
    {
        using var stream = typeof(AboutDialogChrome).Assembly
            .GetManifestResourceStream("Gaya.Plugin.Turian.Resources.logo.png");
        return stream is null ? null : SKImage.FromEncodedData(stream);
    }
}
