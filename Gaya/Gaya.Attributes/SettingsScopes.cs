namespace Gaya.Sdk;

/// <summary>The scopes as an ordered set.</summary>
public static class SettingsScopes
{
    /// <summary>Every single scope, lowest precedence first: a later scope overrides an earlier one.</summary>
    public static IReadOnlyList<SettingsScope> Ascending { get; } =
        [SettingsScope.Studio, SettingsScope.User, SettingsScope.Workspace, SettingsScope.Personal];
}
