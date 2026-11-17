namespace Turian.Editor.Core;

/// <summary>One copy of an external editor found on this machine, or a custom command the user configured.</summary>
/// <param name="EditorId">The id of the <see cref="IExternalEditor"/> that launches it.</param>
/// <param name="DisplayName">What the editor dropdown shows, such as the product and its version.</param>
/// <param name="Executable">The full path of the program to start.</param>
/// <param name="Version">The installed version when it is known, otherwise <c>null</c>.</param>
/// <param name="Arguments">
/// The argument template a custom installation starts with, using the <see cref="EditorArgumentTemplate"/> tokens;
/// <c>null</c> for editors that build their own command line.
/// </param>
public sealed record EditorInstallation(
    string EditorId,
    string DisplayName,
    string Executable,
    string? Version = null,
    string? Arguments = null);
