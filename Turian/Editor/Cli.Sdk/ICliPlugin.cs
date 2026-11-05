using System.CommandLine;
using Microsoft.Extensions.Logging;

namespace Turian.Cli.Sdk;

/// <summary>Contributes commands from an installed brick. Implementations have a public parameterless constructor.</summary>
public interface ICliPlugin
{
    /// <summary>Creates commands without running them or modifying the workspace.</summary>
    /// <param name="context">The owning brick and selected project.</param>
    /// <returns>The commands this plugin contributes.</returns>
    IEnumerable<CliCommand> CreateCommands(CliPluginContext context);
}

/// <summary>The installed brick and host services available while creating commands.</summary>
/// <param name="BrickRoot">The absolute folder of the owning brick.</param>
/// <param name="ProjectRoot">The selected project, or null for commands outside a project.</param>
/// <param name="Logger">The host's diagnostic logger.</param>
public sealed record CliPluginContext(string BrickRoot, string? ProjectRoot, ILogger Logger);

/// <summary>A command contributed at the root or under shared command groups.</summary>
/// <param name="Command">The command with its arguments, options and action.</param>
/// <param name="ParentPath">Parent group names, such as <c>["import"]</c>; empty for a root command.</param>
public sealed record CliCommand(Command Command, params string[] ParentPath);
