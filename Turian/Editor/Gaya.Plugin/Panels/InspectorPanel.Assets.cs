namespace Gaya.Plugin.Turian;

sealed partial class InspectorPanel
{
    /// <summary>
    /// An asset: the file name, then what there is to edit about it — a data asset's own class, the
    /// way a ScriptableObject is edited, or the settings its importer reads when it bakes the file. Content is saved
    /// as it is edited; import settings wait for Apply, because applying them reimports the asset.
    /// </summary>
    void RenderAsset(Gui gui, AssetInspection inspection)
    {
        PrepareAssetForm(gui, inspection);

        using (gui.Node().Expand().Direction(Axis.Vertical).Gap(4f).Padding(6f, 4f).Enter())
        {
            TurianForms.ApplyStyle(gui);
            gui.ScrollY();
            RenderAssetName(gui, inspection);
            if (frameAssets.Count == 1) preview.Draw(gui, inspection.Metadata);
            if (model.Sections.Count == 0) return;
            RenderAssetFields(gui, inspection);
            if (!inspection.IsPayload) RenderAssetActions(gui);
        }
        referenceDrawer.DrawPendingPicker(gui);
        ApplyAssetRequests();
    }

    void PrepareAssetForm(Gui gui, AssetInspection inspection)
    {
        if (gui.Pass == Pass.Pass1Build && (!ReferenceEquals(builtFor, inspection)
            || !builtAssets.SequenceEqual(frameAssets)))
        {
            model = frameAssets.Any(item => item.Target is null || item.IsPayload != inspection.IsPayload)
                ? FormModel.Empty : InspectorForms.BuildForObjects(
                    [.. frameAssets.Select(item => item.Target!)], OnSelectedAssetEdited);
            builtFor = inspection;
            builtAssets = frameAssets;
            builtComponents = 0;
            assetDirty = false;
            LogAssetForm(inspection);
        }

    }

    void LogAssetForm(AssetInspection inspection)
    {
        var section = model.Sections.Count > 0 ? model.Sections[0] : null;
        Log.Logger.LogDebug("asset model built for {Path} ({Type}), targetNull={Null}, fields={Fields}, buttons={Buttons}",
            Path.GetFileName(inspection.AbsolutePath), inspection.Target?.GetType().FullName,
            inspection.Target is null, section?.BodyFields.Count ?? -1, section?.Buttons.Count ?? -1);
    }

    void OnSelectedAssetEdited(object target)
    {
        foreach (var item in frameAssets.Where(item => ReferenceEquals(item.Target, target))) OnAssetEdited(item);
    }

    void RenderAssetName(Gui gui, AssetInspection inspection)
    {
        using (gui.Node(-1, Theme.Scale(24f), "inspector/asset/name").ExpandWidth()
                   .Padding(6, 2).ContentAlignY(0.5f).Enter())
            gui.DrawText(frameAssets.Count > 1 ? $"{frameAssets.Count} Assets" : Path.GetFileName(inspection.AbsolutePath),
                Theme.Text(13), Theme.Ink,
                centerInRect: false);

    }

    void RenderAssetFields(Gui gui, AssetInspection inspection)
    {
        using (gui.Node(-1, Theme.Scale(22f), "inspector/asset/header").ExpandWidth()
                   .Padding(6, 0).ContentAlignY(0.5f).Enter())
        {
            if (gui.Pass == Pass.Pass2Render) gui.DrawBackgroundRect(Theme.Panel, 3);
            gui.DrawText(inspection.Title, Theme.Text(12), Theme.Ink, centerInRect: false);
        }

        using (gui.Node(-1, -1, "inspector/asset/fields").ExpandWidth().Direction(Axis.Vertical)
                   .Padding(8, 2).Gap(2f).Enter())
        {
            var fields = model.Sections[0].BodyFields;
            for (var f = 0; f < fields.Count; f++)
                gui.FormField(fields[f], $"inspector/asset/field{f}", FormContext);
            DrawButtons(gui, model.Sections[0].Buttons, "inspector/asset/button");
        }

    }

    void ApplyAssetRequests()
    {
        if (applyRequested)
        {
            applyRequested = false;
            var applied = true;
            foreach (var item in frameAssets) applied &= inspections.Apply(item);
            if (applied) assetDirty = false;
        }

        if (revertRequested)
        {
            revertRequested = false;
            RevertAssetSelection();
        }
    }

    void RevertAssetSelection()
    {
        var activePath = (frameTarget as AssetInspection)?.AbsolutePath;
        var refreshed = frameAssets.Select(item => inspections.Inspect(item.AbsolutePath))
            .OfType<AssetInspection>().ToArray();
        var active = refreshed.FirstOrDefault(item => item.AbsolutePath == activePath);
        if (Locked)
        {
            lockedAssets = refreshed;
            lockedTarget = active ?? refreshed.FirstOrDefault();
        }
        else inspector.SelectMany(refreshed, active);
    }

    /// <summary>Apply writes the edit and reimports; revert re-reads what is on disk.</summary>
    void RenderAssetActions(Gui gui)
    {
        using (gui.Node(-1, Theme.Scale(28f), "inspector/asset/actions").ExpandWidth()
                   .Direction(Axis.Horizontal).Padding(8, 4).Gap(6f).Enter())
        {
            using (gui.Node().Expand().Enter()) { }

            if (TextButton(gui, "Revert", "inspector/asset/revert", assetDirty)) revertRequested = true;
            if (TextButton(gui, "Apply", "inspector/asset/apply", assetDirty)) applyRequested = true;
        }
    }

}
