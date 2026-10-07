namespace Gaya.Plugin.Turian;

/// <summary>Retains the origin of events, including calls through the shared ambient logger.</summary>
static class LogOrigin
{
    static readonly HashSet<string> StudioAssemblies =
    [
        "Turian.Engine.Core", "Turian.Engine.Attributes", "Turian.Editor.Core", "Turian.Editor.Studio",
        "Turian.Editor.CLI", "Gaya.Plugin.Turian", "Gaya.Host", "Gaya.Sdk", "Gaya.Packages",
        "Guinevere", "Guinevere.Excalibur", "Turian.Engine.UI", "Turian.Editor.UI", "Turian.Engine.Hzb",
    ];

    internal static LogLine Capture(LogLevel level, string message, string category)
    {
        var caller = new StackTrace(true).GetFrames().FirstOrDefault(IsCaller);
        var internalMessage = IsInternal(category);
        if (category == "Turian" && caller is not null)
            internalMessage = StudioAssemblies.Contains(caller.GetMethod()!.DeclaringType!.Assembly.GetName().Name!);
        return new LogLine(level, DateTimeOffset.Now, message)
        {
            Category = category,
            IsInternal = internalMessage,
            SourceFile = caller?.GetFileName(),
            SourceLine = caller?.GetFileLineNumber() ?? 0,
        };
    }

    static bool IsCaller(StackFrame frame)
    {
        var type = frame.GetMethod()?.DeclaringType;
        return type is not null && type != typeof(LogOrigin)
            && type.DeclaringType != typeof(LogBuffer)
            && type.Namespace?.StartsWith("Microsoft.Extensions.Logging", StringComparison.Ordinal) != true;
    }

    internal static bool IsInternal(string origin) => origin == "Editor" || origin == "Turian"
        || origin.StartsWith("Turian.", StringComparison.Ordinal)
        || origin.StartsWith("Gaya.", StringComparison.Ordinal)
        || origin.StartsWith("Guinevere", StringComparison.Ordinal)
        || origin.StartsWith("MASS4.Turian", StringComparison.Ordinal)
        || origin.StartsWith("MASS4.Gaya", StringComparison.Ordinal)
        || origin.StartsWith("Microsoft.", StringComparison.Ordinal);
}
