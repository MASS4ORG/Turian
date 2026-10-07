namespace Gaya.Sdk;

/// <summary>The native window title and encoded application icon selected by a plugin.</summary>
/// <param name="Title">Title displayed by the operating system.</param>
/// <param name="Icon">Encoded image used for the taskbar and application switcher.</param>
public sealed record WindowIdentity(string Title, ReadOnlyMemory<byte> Icon);

/// <summary>Supplies the host's identity for the currently open project.</summary>
public interface IWindowIdentity
{
    /// <summary>The current identity, or null to use Gaya's title and icon.</summary>
    WindowIdentity? Current { get; }
}
