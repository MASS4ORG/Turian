namespace Gaya.Plugin.Turian;

sealed partial class BuildProfilesPanel
{
    readonly Dictionary<string, int> frameSelections = new(StringComparer.Ordinal);
    string? checkedPlatform;
    Task<BuildPlatformStatus>? toolchainCheck;

    bool PlatformField(Gui gui, FormField field, string id)
    {
        if (field.Target is not BuildProfile profile)
        {
            return false;
        }
        switch (field.Name)
        {
            case nameof(BuildProfile.Platform):
                FormControls.Row(gui, field.Label, id, () => ChoosePlatform(gui, profile));
                return true;
            case nameof(BuildProfile.Target):
                FormControls.Row(gui, field.Label, id, () => ChooseTarget(gui, profile));
                return true;
            case nameof(BuildProfile.RuntimeIdentifier):
                FormControls.Row(gui, field.Label, id, () => ChooseRuntime(gui, profile));
                return true;
            default:
                return false;
        }
    }

    void ChoosePlatform(Gui gui, BuildProfile profile)
    {
        var platforms = BuildPlatforms.All();
        var labels = platforms.Select(static platform => platform.DisplayName).ToList();
        var current = PlatformIndex(platforms, labels, profile.Platform);
        var next = Dropdown(gui, [.. labels], current, "buildProfiles/platform");
        if (gui.Pass != Pass.Pass2Render || next < 0 || next == current || next >= platforms.Count)
        {
            return;
        }
        ApplyPlatform(profile, platforms[next]);
        Edited();
    }

    static int PlatformIndex(IReadOnlyList<IBuildPlatform> platforms, List<string> labels, string id)
    {
        var current = platforms.ToList().FindIndex(platform => string.Equals(platform.Id, id, StringComparison.OrdinalIgnoreCase));
        if (current < 0)
        {
            current = platforms.Count;
            labels.Add($"{id} (not installed)");
        }
        return current;
    }

    static void ApplyPlatform(BuildProfile profile, IBuildPlatform platform)
    {
        profile.Platform = platform.Id;
        if (!platform.Targets.Contains(profile.Target))
        {
            profile.Target = platform.Targets[0];
        }
        if (!platform.RuntimeIdentifiers.Contains(profile.RuntimeIdentifier ?? string.Empty))
        {
            profile.RuntimeIdentifier = platform.Id == BuildPlatforms.Desktop ? null : platform.RuntimeIdentifiers.FirstOrDefault();
        }
    }

    void ChooseTarget(Gui gui, BuildProfile profile)
    {
        var platform = FindPlatform(profile.Platform);
        var targets = platform?.Targets.ToList() ?? [profile.Target];
        if (!targets.Contains(profile.Target)) { targets.Insert(0, profile.Target); }
        var current = targets.IndexOf(profile.Target);
        var next = Dropdown(gui, [.. targets.Select(static target => target.ToString())], current, "buildProfiles/target");
        if (gui.Pass == Pass.Pass2Render && next >= 0 && next != current)
        {
            profile.Target = targets[next];
            Edited();
        }
    }

    void ChooseRuntime(Gui gui, BuildProfile profile)
    {
        var platform = FindPlatform(profile.Platform);
        var selected = profile.RuntimeIdentifier?.Trim() ?? string.Empty;
        var ids = RuntimeChoices(platform, selected);
        var labels = ids.Select(static rid => rid.Length == 0 ? "Automatic" : rid).ToArray();
        var current = ids.IndexOf(selected);
        var next = Dropdown(gui, labels, current, "buildProfiles/runtime");
        if (gui.Pass == Pass.Pass2Render && next >= 0 && next != current)
        {
            profile.RuntimeIdentifier = ids[next].Length == 0 ? null : ids[next];
            Edited();
        }
    }

    static List<string> RuntimeChoices(IBuildPlatform? platform, string selected)
    {
        var ids = new[] { string.Empty }.Concat(platform?.RuntimeIdentifiers ?? []).ToList();
        if (!ids.Contains(selected)) { ids.Add(selected); }
        return ids;
    }

    int Dropdown(Gui gui, string[] labels, int current, string id)
    {
        using var scope = gui.Node(-1, Theme.Scale(Theme.RowHeight), id).ExpandWidth().Enter();
        if (gui.Pass == Pass.Pass2Render && frameSelections.TryGetValue(id, out var selection))
        {
            current = selection;
        }
        var next = gui.Dropdown(labels, current, width: 0, height: Theme.Scale(Theme.RowHeight),
            fontSize: Theme.Text(12f), filePath: id);
        if (gui.Pass == Pass.Pass1Build && next != current) { frameSelections[id] = next; }
        if (gui.Pass == Pass.Pass2Render) { frameSelections.Remove(id); }
        return next;
    }

    static IBuildPlatform? FindPlatform(string id) => BuildPlatforms.All()
        .FirstOrDefault(platform => string.Equals(platform.Id, id, StringComparison.OrdinalIgnoreCase));

    void Toolchain(Gui gui)
    {
        if (inspection?.Target is not BuildProfile profile)
        {
            return;
        }
        if (checkedPlatform != profile.Platform)
        {
            checkedPlatform = profile.Platform;
            toolchainCheck = CheckPlatformToolchain(profile.Platform);
        }
        var status = CompletedToolchainStatus(toolchainCheck);
        using (gui.Node(-1, Theme.Scale(56), "buildProfiles/toolchain").ExpandWidth().Direction(Axis.Vertical)
                   .Padding(6, 4).Enter())
        {
            using (gui.Node(-1, Theme.Scale(24), "buildProfiles/toolchain-status").ExpandWidth().Enter())
            {
                gui.DrawText(ToolchainLabel(status),
                    Theme.Text(11), status is { IsReady: false } ? Theme.Error : Theme.InkDim, clip: true);
            }
            if (StudioControls.SmallTextButton(gui, "Check Again", "buildProfiles/check-tools", Theme.Scale(100)))
            {
                checkedPlatform = null;
            }
        }
    }

    static Task<BuildPlatformStatus> CheckPlatformToolchain(string id) => FindPlatform(id) is { } platform
        ? Task.Run(platform.CheckToolchain)
        : Task.FromResult(new BuildPlatformStatus(false, $"Install the {id} platform brick."));

    internal static BuildPlatformStatus? CompletedToolchainStatus(Task<BuildPlatformStatus>? check) => check switch
    {
        { IsCompletedSuccessfully: true } completed => completed.Result,
        { IsFaulted: true } failed => new(false, failed.Exception.GetBaseException().Message),
        _ => null,
    };

    static string ToolchainLabel(BuildPlatformStatus? status) => status is null ? "Checking toolchain…"
        : $"{(status.IsReady ? "Ready" : "Missing tools")}: {status.Message}";
}
