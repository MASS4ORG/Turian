namespace Turian.Editor.Core;

/// <summary>Edits asset labels through undoable metadata changes and synchronizes the import catalog.</summary>
public sealed class AssetLabelService(AssetImporter importer, UndoService undo)
{
    sealed record Edit(AssetEntry Entry, string Before, string After, string[] BeforeLabels, string[] AfterLabels);

    /// <summary>Returns the distinct labels currently carried by browser assets.</summary>
    public static IReadOnlyList<string> Collect(IEnumerable<AssetEntry> entries) =>
        [.. entries.SelectMany(entry => entry.AssetMetadata?.Labels ?? [])
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

    /// <summary>Adds or removes a label on writable selected assets as one undoable edit.</summary>
    public bool Set(IEnumerable<AssetEntry> selection, string label, bool present)
    {
        label = label.Trim();
        if (label.Length == 0) return false;
        try
        {
            var edits = selection.Where(CanEdit).DistinctBy(entry => entry.AbsolutePath)
                .Select(entry => Prepare(entry, label, present)).ToArray();
            if (edits.Length == 0) return false;
            undo.Perform("Asset labels", [], () => WriteAll(edits, before: false), () => WriteAll(edits, before: true),
                document: UndoService.ProjectDocument);
            return true;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Logger.LogWarning(ex, "Could not edit asset labels");
            return false;
        }
    }

    static bool CanEdit(AssetEntry entry) => !entry.IsReadOnly && !entry.IsDirectory && entry.AssetMetadata is not null;

    static Edit Prepare(AssetEntry entry, string label, bool present)
    {
        var text = File.ReadAllText(entry.AbsolutePath + ".meta");
        var json = JsonNode.Parse(text)?.AsObject() ?? throw new JsonException("Asset metadata must be an object.");
        var before = entry.AssetMetadata!.Labels.ToArray();
        var after = present ? before.Append(label).Distinct().ToArray() : [.. before.Where(value => value != label)];
        json["Labels"] = JsonSerializer.SerializeToNode(after);
        return new Edit(entry, text, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), before, after);
    }

    void WriteAll(IReadOnlyList<Edit> edits, bool before)
    {
        var completed = 0;
        try
        {
            foreach (var edit in edits)
            {
                Write(edit, before);
                completed++;
            }
        }
        catch
        {
            for (var i = completed - 1; i >= 0; i--) Write(edits[i], !before);
            throw;
        }
    }

    void Write(Edit edit, bool before)
    {
        File.WriteAllText(edit.Entry.AbsolutePath + ".meta", before ? edit.Before : edit.After);
        edit.Entry.AssetMetadata!.Labels = [.. before ? edit.BeforeLabels : edit.AfterLabels];
        importer.ReimportNow(edit.Entry.AbsolutePath);
    }
}
