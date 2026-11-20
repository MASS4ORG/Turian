namespace Turian.Editor.Core;

/// <summary>Starts an editor without blocking the caller and tells a failed start from a running editor.</summary>
public static class EditorProcess
{
    /// <summary>How long a started program may take to exit before it is assumed to be the running editor.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Starts <paramref name="command"/> on a worker thread. A program still running after the grace period is the
    /// editor itself; one that exits with a non-zero code during it, as a launcher script does on a bad argument, is a
    /// failure.
    /// </summary>
    /// <param name="command">The command to start.</param>
    /// <param name="displayName">The editor's name, used in the failure message.</param>
    /// <param name="cancellationToken">Stops waiting; the started program keeps running.</param>
    /// <param name="grace">How long to wait for an early exit; <see cref="DefaultGrace"/> when <c>null</c>.</param>
    /// <exception cref="InvalidOperationException">The program could not be started or exited with an error.</exception>
    public static async Task StartAsync(EditorCommand command, string displayName,
        CancellationToken cancellationToken = default, TimeSpan? grace = null)
    {
        ArgumentNullException.ThrowIfNull(command);

        Process process;
        try
        {
            process = await Task.Run(() => Process.Start(command.ToStartInfo()), cancellationToken)
                .ConfigureAwait(false) ?? throw new InvalidOperationException($"{displayName} did not start.");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or IOException
                                              or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"{displayName} could not be started: {exception.Message}", exception);
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(grace ?? DefaultGrace);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return;
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{displayName} exited with code {process.ExitCode}.");
            }
        }
    }
}
