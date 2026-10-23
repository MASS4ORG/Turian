namespace Gaya.Plugin.Turian;

/// <summary>Retains the origin of events, including calls through the shared ambient logger.</summary>
static class LogOrigin
{
    internal static LogLine Capture(LogLevel level, string message, string category)
    {
        var caller = new StackTrace(true).GetFrames().FirstOrDefault(IsCaller);
        var internalMessage = IsInternal(category);
        if (category == "Turian" && caller is not null)
        {
            internalMessage = IsStudioAssembly(caller.GetMethod()!.DeclaringType!.Assembly);
        }

        return new LogLine(level, DateTimeOffset.Now, message)
        {
            Category = category,
            IsInternal = internalMessage,
            SourceFile = caller?.GetFileName(),
            SourceLine = caller?.GetFileLineNumber() ?? 0,
        };
    }

    /// <summary>Whether this repository built the assembly: <c>Directory.Build.targets</c> stamps every non-test project.</summary>
    static bool IsStudioAssembly(Assembly assembly) =>
        assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(static a => a is { Key: "Mass4.Origin", Value: "Turian" });

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
