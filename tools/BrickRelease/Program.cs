using System.Runtime.InteropServices;

namespace Mass4.BrickRelease;

static class Program
{
    internal static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        using var terminate = OperatingSystem.IsWindows() ? null :
            PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
        using var interrupt = OperatingSystem.IsWindows() ? null :
            PosixSignalRegistration.Create(PosixSignal.SIGINT, context =>
            {
                context.Cancel = true;
                cancellation.Cancel();
            });
        try
        {
            await ExecuteAsync(args, Environment.GetEnvironmentVariable("BRICK_RELEASE_ROOT") ??
                Directory.GetCurrentDirectory(), cancellation.Token);
            return 0;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException or ArgumentException
                                     or OperationCanceledException or System.Text.Json.JsonException
                                     or System.ComponentModel.Win32Exception or FormatException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    internal static Task ExecuteAsync(string[] args, string root, CancellationToken cancellationToken)
    {
        if (args.Length == 0)
        {
            throw Usage();
        }

        return args[0] switch
        {
            "qualify" => QualifyCommand(args),
            "validate" => ValidateCommand(args),
            "sign" => SignCommand(args, root, cancellationToken),
            "consumer" => ConsumerCommand(args, cancellationToken),
            _ => throw Usage(),
        };
    }

    static Task QualifyCommand(string[] args)
    {
        RequireArguments(args, 5, 7);
        if (args.Length >= 6 && args[5] != "--github-output")
        {
            throw Usage();
        }

        ReleaseArchive.Qualify(args[1], args[2], args[3], args[4],
            args.Length >= 6 ? Required("GITHUB_OUTPUT") : null, args.Length == 7 ? args[6] : "");
        return Task.CompletedTask;
    }

    static Task ValidateCommand(string[] args)
    {
        RequireArguments(args, 3, 3);
        ReleaseArchive.Validate(args[1], args[2]);
        return Task.CompletedTask;
    }

    static Task SignCommand(string[] args, string root, CancellationToken cancellationToken)
    {
        // The build is both fields or none: the store refuses a partial one.
        if (args.Length is not (4 or 7) || (args.Length == 7 && args[4] != "--build"))
        {
            throw Usage();
        }

        var build = args.Length == 7 ? ReleaseArchive.ValidateBuild(args[5], args[6]) : null;
        return SignAsync(root, args[1], args[2], args[3], build, cancellationToken);
    }

    static Task ConsumerCommand(string[] args, CancellationToken cancellationToken)
    {
        RequireArguments(args, 2, 3);
        return args.Length == 3
            ? ConsumerSmoke.RunAsync(args[1], args[2], cancellationToken)
            : ConsumerSmoke.RunLocalAsync(args[1], cancellationToken);
    }

    static void RequireArguments(string[] args, int minimum, int maximum)
    {
        if (args.Length < minimum || args.Length > maximum)
        {
            throw Usage();
        }
    }

    static ArgumentException Usage() => new("Expected qualify ARCHIVE TAG REVISION OUTPUT [--github-output [PREFIX]], " +
                                            "validate ARCHIVE QUALIFICATION, " +
                                            "sign ARCHIVE QUALIFICATION OUTPUT [--build IMAGE@sha256:DIGEST TURIAN_VERSION], " +
                                            "or consumer ARCHIVE [IMAGE] (without IMAGE, with the turian-cli on PATH).");

    static async Task SignAsync(string root, string archive, string qualification, string output, ReleaseBuild? build,
        CancellationToken cancellationToken)
    {
        var key = Required("MASS4_BRICKS_CI_SIGNING_KEY");
        Environment.SetEnvironmentVariable("MASS4_BRICKS_CI_SIGNING_KEY", null);
        await ReleaseSigner.SignAsync(root, archive, qualification, output,
            Required("MASS4_BRICKS_CI_PUBLIC_KEY"), key, Required("RUNNER_TEMP"), cancellationToken, build);
    }

    static string Required(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
        ? value : throw new InvalidOperationException($"Missing {name}.");
}
