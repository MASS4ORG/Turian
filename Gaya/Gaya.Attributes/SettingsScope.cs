namespace Gaya.Sdk;

/// <summary>
/// Where a setting's value is stored, as layers: a member resolves from the highest scope that holds it.
/// The flag order is the precedence, lowest first.
/// </summary>
[Flags]
public enum SettingsScope
{
    /// <summary>No scope.</summary>
    None = 0,

    /// <summary>Machine-wide defaults, beneath everything else.</summary>
    Studio = 1,

    /// <summary>With the user, beside the editor's other preferences. Follows them across projects.</summary>
    User = 2,

    /// <summary>With the project, so it is shared by everyone who opens it.</summary>
    Workspace = 4,

    /// <summary>
    /// The user's own values for one project. Kept with the user, keyed by the project's identity,
    /// and never written inside the project directory, so it is never committed.
    /// </summary>
    Personal = 8,
}
