namespace Turian.Editor.Core;

/// <summary>A program and its arguments, kept apart so no path ever needs shell quoting.</summary>
/// <param name="FileName">The program to start.</param>
/// <param name="Arguments">One entry per argument, passed to the program unchanged.</param>
public sealed record EditorCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>Process settings that start the program without a shell or a console window.</summary>
    /// <returns>The start information.</returns>
    public ProcessStartInfo ToStartInfo()
    {
        var start = new ProcessStartInfo(FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }
}
