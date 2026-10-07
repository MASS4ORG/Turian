namespace Turian.Editor.Core;

/// <summary>Resolves the minimum Studio logging level from process arguments.</summary>
public static class StudioLogOptions
{
    /// <summary>Defaults to Information; accepts a named level through --log-level.</summary>
    /// <param name="arguments">The process arguments.</param>
    /// <returns>The minimum level sent to the console and Output panel.</returns>
    public static LogLevel MinimumLevel(IReadOnlyList<string> arguments)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (arguments[index] != "--log-level") continue;
            if (++index == arguments.Count) throw new ArgumentException("--log-level requires a logging level.");
            if (Enum.GetNames<LogLevel>().Contains(arguments[index], StringComparer.OrdinalIgnoreCase))
                return Enum.Parse<LogLevel>(arguments[index], ignoreCase: true);
            throw new ArgumentException($"Unknown logging level '{arguments[index]}'.");
        }
        return LogLevel.Information;
    }
}
