using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Turian.Tests;

/// <summary>Process-level quit control shared by the players: quitting on Ctrl+C and SIGTERM.</summary>
[Collection(SerialTests.Name)]
public sealed class ApplicationTests
{
    /// <summary>
    /// QuitOnInterrupt cancels the quit token on SIGTERM, and again on SIGINT after a new session; once its handle
    /// is disposed it stops listening.
    /// </summary>
    [Fact]
    public async Task QuitOnInterruptFollowsInterruptsUntilDisposed()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "raising a signal needs the libc kill entry point");

        var directory = Directory.CreateTempSubdirectory("turian-interrupt-test-");
        try
        {
            const string source = """
                using System;
                using System.Runtime.InteropServices;
                using Turian.Engine.Core;

                static class SignalProbe
                {
                    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
                    static extern int Kill(int pid, int signal);

                    static int Main()
                    {
                        foreach (var signal in new[] { 15, 2 })
                        {
                            Application.BeginSession(headless: true);
                            using (Application.QuitOnInterrupt())
                            {
                                if (Kill(Environment.ProcessId, signal) != 0) return 1;
                                if (!Application.QuitToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(10))) return 2;
                            }
                        }

                        Application.BeginSession(headless: false);
                        using var ignoreTerminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM,
                            context => context.Cancel = true);
                        using var ignoreInterrupt = PosixSignalRegistration.Create(PosixSignal.SIGINT,
                            context => context.Cancel = true);
                        foreach (var signal in new[] { 15, 2 })
                        {
                            if (Kill(Environment.ProcessId, signal) != 0) return 3;
                            if (Application.QuitToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(200))) return 4;
                        }

                        return 0;
                    }
                }
                """;
            var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
                .Split(Path.PathSeparator).Select(static path => MetadataReference.CreateFromFile(path));
            var assembly = Path.Combine(directory.FullName, "SignalProbe.dll");
            var compilation = CSharpCompilation.Create("SignalProbe",
                [CSharpSyntaxTree.ParseText(source, cancellationToken: TestContext.Current.CancellationToken)],
                references, new CSharpCompilationOptions(OutputKind.ConsoleApplication));
            var result = compilation.Emit(assembly, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
            foreach (var dependency in Directory.EnumerateFiles(
                         Path.GetDirectoryName(typeof(Application).Assembly.Location)!, "*.dll"))
                File.CreateSymbolicLink(Path.Combine(directory.FullName, Path.GetFileName(dependency)), dependency);
            File.Copy(Path.ChangeExtension(typeof(ApplicationTests).Assembly.Location, ".runtimeconfig.json"),
                Path.ChangeExtension(assembly, ".runtimeconfig.json"));

            var start = new ProcessStartInfo(DotnetLocator.Find())
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add(assembly);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                throw;
            }

            Assert.True(process.ExitCode == 0,
                $"Signal probe exited with {process.ExitCode}: {await error}{await output}");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    /// <summary>The first interrupt quits the game; one arriving while it already quits lets the process end.</summary>
    [Fact]
    public void ASecondInterruptEndsTheProcess()
    {
        Application.BeginSession(headless: true);

        Assert.True(Application.Interrupt());
        Assert.True(Application.QuitToken.IsCancellationRequested);
        Assert.False(Application.Interrupt());

        Application.BeginSession(headless: false);
    }

}
