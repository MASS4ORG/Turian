using System.Diagnostics;

namespace Mass4.BrickRelease;

static class ToolProcess
{
    internal static async Task<string> RunAsync(string executable, IEnumerable<string> arguments,
        CancellationToken cancellationToken, string? input = null)
    {
        using var process = Start(executable, arguments);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            await CommunicateAsync(process, input, cancellationToken);
            return await ResultAsync(process, executable, output, error);
        }
        finally
        {
            await StopAndDrainAsync(process, output, error);
        }
    }

    static Process Start(string executable, IEnumerable<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("SSH_AUTH_SOCK");
        start.Environment.Remove("SSH_AGENT_PID");
        start.Environment.Remove("MASS4_BRICKS_CI_SIGNING_KEY");
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start release tool.");
    }

    static async Task CommunicateAsync(Process process, string? input, CancellationToken cancellationToken)
    {
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input.AsMemory(), cancellationToken);
        }

        process.StandardInput.Close();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        await process.WaitForExitAsync(timeout.Token);
    }

    static async Task<string> ResultAsync(Process process, string executable, Task<string> output, Task<string> error)
    {
        await Task.WhenAll(output, error);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{executable} failed; no credentials or arguments are logged.");
        }

        return output.Result;
    }

    static async Task StopAndDrainAsync(Process process, Task<string> output, Task<string> error)
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
        }

        await Task.WhenAll(output, error);
    }
}
