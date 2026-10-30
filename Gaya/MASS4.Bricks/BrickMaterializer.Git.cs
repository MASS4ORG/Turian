namespace MASS4.Bricks;

public sealed partial class BrickMaterializer
{
    static async Task GitAsync(BrickMaterializationSource source, string? token, string payload,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(payload);
        await RunGitAsync(["init", "--quiet", payload], source, token, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(["-C", payload, "fetch", "--quiet", "--depth=1", "--no-tags", "--",
            source.Url!, source.Ref!], source, token, cancellationToken).ConfigureAwait(false);
        await RunGitAsync(["-C", payload, "checkout", "--quiet", "--detach", "FETCH_HEAD"], source, token,
            cancellationToken).ConfigureAwait(false);
        foreach (var entry in Directory.EnumerateFileSystemEntries(payload, "*", SearchOption.AllDirectories))
        {
            if (new FileInfo(entry).LinkTarget is not null)
            {
                throw new PackageException("Materialized git sources may not contain symbolic links.");
            }
        }

        // Git metadata is unnecessary for producer project references and may contain local transport configuration.
        Directory.Delete(Path.Combine(payload, ".git"), recursive: true);
    }

    static async Task RunGitAsync(string[] arguments, BrickMaterializationSource source, string? token,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("credential.helper=");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("http.followRedirects=false");
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("SOURCE_DEP_TOKEN");
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (!string.IsNullOrEmpty(token))
        {
            var scope = new Uri(source.Url!).GetLeftPart(UriPartial.Authority);
            start.Environment["GIT_CONFIG_COUNT"] = "1";
            start.Environment["GIT_CONFIG_KEY_0"] = $"http.{scope}/.extraheader";
            start.Environment["GIT_CONFIG_VALUE_0"] =
                $"Authorization: Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"x-access-token:{token}"))}";
        }

        using var process = new Process { StartInfo = start };
        try
        {
            process.Start();
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            _ = await output.ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                // Remote output can echo credentials; report the operation without repeating it.
                throw new PackageException($"Git could not materialize the pinned dependency (exit {process.ExitCode}). Check its URL, ref and read token.");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new PackageException("Git is required to materialize a git dependency.", ex);
        }
    }
}
