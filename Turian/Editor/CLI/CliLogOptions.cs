namespace Turian.Editor.CLI;

/// <summary>Resolves CLI logging before commands and plugins start.</summary>
public static class CliLogOptions
{
    /// <summary>Defaults to Information and accepts named levels through --verbosity or --log-level.</summary>
    /// <param name="arguments">The command-line arguments.</param>
    /// <returns>The minimum level sent to the console.</returns>
    public static LogLevel MinimumLevel(IReadOnlyList<string> arguments)
    {
        var level = LogLevel.Information;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var separator = argument.IndexOf('=');
            var option = separator < 0 ? argument : argument[..separator];
            if (argument == "--") { break; }
            if (option is not ("--verbosity" or "--log-level")) { continue; }
            var value = separator >= 0 ? argument[(separator + 1)..]
                : ++index < arguments.Count ? arguments[index] : string.Empty;
            if (!Enum.GetNames<LogLevel>().Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{option} requires Trace, Debug, Information, Warning, Error, Critical or None.");
            }
            level = Enum.Parse<LogLevel>(value, ignoreCase: true);
        }
        return level;
    }
}
