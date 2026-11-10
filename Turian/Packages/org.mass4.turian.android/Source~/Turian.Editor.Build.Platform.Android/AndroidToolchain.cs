using System.Diagnostics;
using System.Globalization;

namespace Turian.Editor.Core;

/// <summary>
/// Finds the Android SDK, the JDK and adb the way a person installed them, and talks to devices through adb, so an
/// export or an installation needs no environment set up by hand.
/// </summary>
public static class AndroidToolchain
{
    /// <summary>The oldest JDK the .NET Android tools build with.</summary>
    public const int MinimumJdk = 17;

    static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(10);

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    static string Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    /// <summary>The Android SDK: the configured folder, ANDROID_HOME, ANDROID_SDK_ROOT, then the usual folders.</summary>
    /// <param name="configured">The folder the user set, or empty to search.</param>
    /// <returns>The SDK folder, or null when none is found.</returns>
    public static string? FindSdk(string? configured) =>
        FindSdk(configured, Environment.GetEnvironmentVariable, DefaultSdkFolders());

    /// <summary>The first candidate that holds an Android SDK.</summary>
    internal static string? FindSdk(string? configured, Func<string, string?> variable, IEnumerable<string> defaults) =>
        new[] { configured, variable("ANDROID_HOME"), variable("ANDROID_SDK_ROOT") }.Concat(defaults)
            .FirstOrDefault(IsSdk);

    /// <summary>A folder with the parts of an SDK that export or installation use.</summary>
    internal static bool IsSdk(string? folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && new[] { "platform-tools", "platforms", "build-tools" }.Any(part => Directory.Exists(Path.Combine(folder, part)));

    /// <summary>Where Android Studio and the command-line tools put the SDK on this system.</summary>
    internal static IEnumerable<string> DefaultSdkFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Android", "Sdk");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Android", "android-sdk");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Android", "android-sdk");
            yield break;
        }

        if (OperatingSystem.IsMacOS())
        {
            yield return Path.Combine(Home, "Library", "Android", "sdk");
            yield break;
        }

        yield return Path.Combine(Home, "Android", "Sdk");
        yield return Path.Combine(Home, "android-sdk");
        yield return "/opt/android-sdk";
        yield return "/usr/lib/android-sdk";
    }

    /// <summary>
    /// The JDK: the configured folder, JAVA_HOME, Android Studio's bundled one, then installed JDKs of version
    /// <see cref="MinimumJdk"/> or newer, oldest first because that is the one the Android tools are tested with.
    /// </summary>
    /// <param name="configured">The folder the user set, or empty to search.</param>
    /// <returns>The JDK folder, or null when none is found.</returns>
    public static string? FindJdk(string? configured) =>
        FindJdk(configured, Environment.GetEnvironmentVariable, DefaultJdkFolders());

    /// <summary>The first candidate that holds a JDK new enough.</summary>
    internal static string? FindJdk(string? configured, Func<string, string?> variable, IEnumerable<string> defaults) =>
        new[] { configured, variable("JAVA_HOME") }.Concat(defaults).FirstOrDefault(IsJdk);

    /// <summary>A folder holding a Java compiler whose version is known to be new enough, or unknown.</summary>
    internal static bool IsJdk(string? folder) =>
        !string.IsNullOrWhiteSpace(folder)
        && File.Exists(Path.Combine(folder, "bin", Executable("javac")))
        && (JdkVersion(folder) ?? MinimumJdk) >= MinimumJdk;

    /// <summary>The major version a JDK's <c>release</c> file names, or null when it names none.</summary>
    internal static int? JdkVersion(string folder)
    {
        var release = Path.Combine(folder, "release");
        if (!File.Exists(release))
        {
            return null;
        }

        var line = File.ReadLines(release).FirstOrDefault(static line => line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal));
        var version = line?["JAVA_VERSION=".Length..].Trim('"', ' ');
        var major = version?.Split('.')[0];
        return int.TryParse(major == "1" ? version!.Split('.')[1] : major, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    /// <summary>Android Studio's bundled JDK, then the folders JDK installers use, oldest version first.</summary>
    internal static IEnumerable<string> DefaultJdkFolders()
    {
        var studio = OperatingSystem.IsWindows()
            ? [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Android", "Android Studio", "jbr")]
            : OperatingSystem.IsMacOS()
                ? ["/Applications/Android Studio.app/Contents/jbr/Contents/Home"]
                : new[] { Path.Combine(Home, "android-studio", "jbr"), "/opt/android-studio/jbr",
                    "/usr/local/android-studio/jbr", "/snap/android-studio/current/jbr" };
        foreach (var folder in studio)
        {
            yield return folder;
        }

        var roots = OperatingSystem.IsWindows()
            ? new[] { "Microsoft", "Eclipse Adoptium", "Java", "Zulu" }.Select(vendor =>
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), vendor))
            : OperatingSystem.IsMacOS()
                ? ["/Library/Java/JavaVirtualMachines"]
                : ["/usr/lib/jvm"];
        var installed = roots.Where(Directory.Exists).SelectMany(Directory.EnumerateDirectories)
            .Select(folder => OperatingSystem.IsMacOS() ? Path.Combine(folder, "Contents", "Home") : folder)
            .Select(folder => (Folder: folder, Version: JdkVersion(folder) ?? int.MaxValue))
            .Where(static jdk => jdk.Version >= MinimumJdk)
            .OrderBy(static jdk => jdk.Version)
            .ThenBy(static jdk => jdk.Folder, StringComparer.Ordinal);
        foreach (var (folder, _) in installed)
        {
            yield return folder;
        }
    }

    /// <summary>The adb of an SDK, or null when the SDK has no platform tools.</summary>
    /// <param name="sdk">The SDK folder.</param>
    /// <returns>The adb executable, or null.</returns>
    public static string? FindAdb(string? sdk)
    {
        if (sdk is null)
        {
            return null;
        }

        var adb = Path.Combine(sdk, "platform-tools", Executable("adb"));
        return File.Exists(adb) ? adb : null;
    }

    /// <summary>Every device adb knows, from <c>adb devices -l</c>, ready or not.</summary>
    /// <param name="adb">The adb executable.</param>
    /// <returns>The devices with their states.</returns>
    public static IReadOnlyList<AdbDevice> ListAllDevices(string adb) =>
        ParseDevices(Run(adb, ["devices", "-l"], QueryTimeout).Checked("devices -l"));

    /// <summary>The devices ready to install on.</summary>
    /// <param name="adb">The adb executable.</param>
    /// <returns>The ready devices, named by model and serial.</returns>
    public static IReadOnlyList<BuildDevice> ListDevices(string adb) =>
        [.. ListAllDevices(adb).Where(static device => device.Ready).Select(static device => device.ToBuildDevice())];

    /// <summary>The devices an <c>adb devices -l</c> listing shows, with their states and models.</summary>
    internal static IReadOnlyList<AdbDevice> ParseDevices(string listing) =>
    [
        .. Lines(listing)
            .Where(static line => !line.StartsWith("List of devices", StringComparison.Ordinal) && !line.StartsWith('*'))
            .Select(static line => line.Split(' ', '\t').Where(static part => part.Length > 0).ToArray())
            .Where(static parts => parts.Length >= 2)
            .Select(static parts => new AdbDevice(parts[0], parts[1],
                parts.FirstOrDefault(static part => part.StartsWith("model:", StringComparison.Ordinal))?
                    ["model:".Length..].Replace('_', ' '))),
    ];

    /// <summary>Picks the device to install on: the requested one, else the only ready one.</summary>
    /// <param name="devices">Every device adb knows.</param>
    /// <param name="requested">The device asked for, or empty.</param>
    /// <returns>The device's serial.</returns>
    /// <exception cref="InvalidOperationException">When the choice is impossible or ambiguous; the message says why.</exception>
    public static string ChooseDevice(IReadOnlyList<AdbDevice> devices, string? requested)
    {
        var ready = devices.Where(static device => device.Ready).ToList();
        if (!string.IsNullOrWhiteSpace(requested))
        {
            return devices.FirstOrDefault(device => device.Serial == requested) switch
            {
                { Ready: true } => requested,
                { State: "unauthorized" } device => throw new InvalidOperationException(
                    $"{device.Name} has not authorized this computer: accept the USB debugging prompt on its screen."),
                { } device => throw new InvalidOperationException(
                    $"{device.Name} is {device.State}: reconnect it and check that USB debugging is on."),
                null => throw new InvalidOperationException(
                    $"Android device {requested} is not connected. Ready: {Describe(ready)}."),
            };
        }

        if (ready.Count == 1)
        {
            return ready[0].Serial;
        }

        if (ready.Count > 1)
        {
            throw new InvalidOperationException(
                $"Several Android devices are connected: {Describe(ready)}. Choose one with --device or in Settings, Build, Android.");
        }

        throw new InvalidOperationException(devices.FirstOrDefault(static device => device.State == "unauthorized") is { } locked
            ? $"{locked.Name} has not authorized this computer: accept the USB debugging prompt on its screen."
            : "No Android device is ready. Connect one with USB debugging on and accept the prompt on its screen.");
    }

    static string Describe(IReadOnlyList<AdbDevice> devices) =>
        devices.Count == 0 ? "none" : string.Join(", ", devices.Select(static device => device.Name));

    /// <summary>Installs an APK on a device, replacing an installed copy.</summary>
    /// <param name="adb">The adb executable.</param>
    /// <param name="device">The device's serial.</param>
    /// <param name="package">The APK.</param>
    public static void Install(string adb, string device, string package)
    {
        var result = Run(adb, ["-s", device, "install", "-r", "--streaming", package], InstallTimeout);
        if (InstallFailure(result.Output + result.Error) is { } failure)
        {
            throw new InvalidOperationException(failure);
        }

        result.Checked("install");
    }

    /// <summary>What went wrong in an <c>adb install</c> output, in words a person can act on, or null on success.</summary>
    internal static string? InstallFailure(string output)
    {
        var match = System.Text.RegularExpressions.Regex.Match(output, @"Failure \[(?<code>[A-Z_]+)(?::\s*(?<detail>[^\]]*))?\]");
        if (!match.Success)
        {
            return null;
        }

        var code = match.Groups["code"].Value;
        var advice = code switch
        {
            "INSTALL_FAILED_UPDATE_INCOMPATIBLE" =>
                "The installed copy was signed with another key: uninstall it from the device, then install again.",
            "INSTALL_FAILED_VERSION_DOWNGRADE" =>
                "The installed copy has a higher Android version code: raise the version code or uninstall it.",
            "INSTALL_FAILED_INSUFFICIENT_STORAGE" => "The device is out of storage: free some space.",
            "INSTALL_FAILED_NO_MATCHING_ABIS" =>
                "The build is for another processor: choose the device's architecture in the build profile.",
            "INSTALL_FAILED_OLDER_SDK" => "The device's Android is too old: Turian needs Android 13 or newer.",
            "INSTALL_FAILED_USER_RESTRICTED" or "INSTALL_FAILED_ABORTED" =>
                "The device refused the installation: allow installing over USB on its screen or in its developer options.",
            "INSTALL_PARSE_FAILED_NO_CERTIFICATES" or "INSTALL_PARSE_FAILED_INCONSISTENT_CERTIFICATES" =>
                "The APK is not signed correctly: export it again.",
            _ => null,
        };
        var detail = match.Groups["detail"].Value.Trim();
        return $"The device did not install the app ({code}{(detail.Length > 0 ? ": " + detail : "")}).{(advice is null ? "" : " " + advice)}";
    }

    /// <summary>Starts an installed app's launcher activity and waits until it is drawn.</summary>
    /// <param name="adb">The adb executable.</param>
    /// <param name="device">The device's serial.</param>
    /// <param name="applicationId">The application id.</param>
    /// <returns>How long the start took in milliseconds, when the device reports it.</returns>
    public static int? Start(string adb, string device, string applicationId)
    {
        var resolved = Run(adb, ["-s", device, "shell", "cmd", "package", "resolve-activity", "--brief", "-c",
            "android.intent.category.LAUNCHER", applicationId], QueryTimeout).Checked("resolve-activity");
        var component = LauncherComponent(resolved, applicationId) ?? throw new InvalidOperationException(
            $"{applicationId} has no launcher activity on {device}; is it installed?");
        var started = ParseStart(Run(adb, ["-s", device, "shell", "am", "start", "-W", "-n", component], InstallTimeout)
            .Checked("am start"));
        return started.Ok ? started.TotalMilliseconds : throw new InvalidOperationException(
            $"{applicationId} did not start on {device}: {started.Error}");
    }

    /// <summary>The <c>package/activity</c> line of a <c>resolve-activity --brief</c> output, or null.</summary>
    internal static string? LauncherComponent(string output, string applicationId) =>
        Lines(output).LastOrDefault(line => line.StartsWith(applicationId + "/", StringComparison.Ordinal));

    /// <summary>Whether an <c>am start -W</c> output reports a start, how long it took, and the error otherwise.</summary>
    internal static (bool Ok, int? TotalMilliseconds, string? Error) ParseStart(string output)
    {
        var lines = Lines(output).ToList();
        if (lines.FirstOrDefault(static line => line.StartsWith("Error", StringComparison.Ordinal)) is { } error)
        {
            return (false, null, error);
        }

        var status = lines.FirstOrDefault(static line => line.StartsWith("Status:", StringComparison.Ordinal))?
            ["Status:".Length..].Trim();
        var total = lines.FirstOrDefault(static line => line.StartsWith("TotalTime:", StringComparison.Ordinal))?
            ["TotalTime:".Length..].Trim();
        int? milliseconds = int.TryParse(total, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value : null;
        return status is null or "ok" ? (true, milliseconds, null) : (false, milliseconds, $"status {status}");
    }

    /// <summary>The process id a <c>pidof</c> output names, or null when the app is not running.</summary>
    internal static int? ParsePid(string output) =>
        Lines(output).SelectMany(static line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Select(static part => int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                ? pid : (int?)null)
            .FirstOrDefault(static pid => pid is not null);

    /// <summary>The app's process on the device, or null when it is not running.</summary>
    public static int? FindPid(string adb, string device, string applicationId) =>
        ParsePid(Run(adb, ["-s", device, "shell", "pidof", applicationId], QueryTimeout).Output);

    /// <summary>
    /// Writes the running app's log to <paramref name="write"/> until it exits or <paramref name="cancellationToken"/>
    /// is cancelled, then stops the app if it is still running.
    /// </summary>
    /// <param name="adb">The adb executable.</param>
    /// <param name="device">The device's serial.</param>
    /// <param name="applicationId">The application id.</param>
    /// <param name="write">Receives each log line.</param>
    /// <param name="cancellationToken">Ends following, as Ctrl+C does.</param>
    public static async Task FollowAsync(string adb, string device, string applicationId, Action<string> write,
        CancellationToken cancellationToken)
    {
        int? pid = null;
        for (var attempt = 0; attempt < 20 && pid is null && !cancellationToken.IsCancellationRequested; attempt++)
        {
            pid = FindPid(adb, device, applicationId);
            if (pid is null)
            {
                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }

        if (pid is null)
        {
            write($"{applicationId} is not running on {device}.");
            return;
        }

        using var logcat = Process.Start(Start(adb, ["-s", device, "logcat", "-v", "brief", $"--pid={pid}"]))
            ?? throw new InvalidOperationException($"Could not start {adb}.");
        logcat.OutputDataReceived += (_, line) =>
        {
            if (line.Data is not null)
            {
                write(line.Data);
            }
        };
        logcat.BeginOutputReadLine();
        try
        {
            while (!cancellationToken.IsCancellationRequested && FindPid(adb, device, applicationId) == pid)
            {
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Ctrl+C: stop the game below.
        }
        finally
        {
            if (!logcat.HasExited)
            {
                logcat.Kill(entireProcessTree: true);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            Run(adb, ["-s", device, "shell", "am", "force-stop", applicationId], QueryTimeout);
            write($"Stopped {applicationId}.");
        }
        else
        {
            write($"{applicationId} exited.");
        }
    }

    static IEnumerable<string> Lines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static ProcessStartInfo Start(string adb, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(adb)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        return start;
    }

    static AdbResult Run(string adb, IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        using var process = Process.Start(Start(adb, arguments)) ?? throw new InvalidOperationException($"Could not start {adb}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(
                $"adb {string.Join(' ', arguments)} did not finish in {timeout.TotalSeconds:0} s; check the device's connection.");
        }

        return new AdbResult(process.ExitCode, output.GetAwaiter().GetResult(), error.GetAwaiter().GetResult());
    }

    readonly record struct AdbResult(int ExitCode, string Output, string Error)
    {
        public string Checked(string command) => ExitCode == 0
            ? Output
            : throw new InvalidOperationException($"adb {command} failed: {(Output + Error).Trim()}");
    }
}

/// <summary>A device as <c>adb devices -l</c> lists it.</summary>
/// <param name="Serial">The serial adb addresses it by.</param>
/// <param name="State">Its state: <c>device</c> when ready, else <c>unauthorized</c>, <c>offline</c> and so on.</param>
/// <param name="Model">Its model, when adb reports one.</param>
public sealed record AdbDevice(string Serial, string State, string? Model)
{
    /// <summary>Whether it accepts installations.</summary>
    public bool Ready => State == "device";

    /// <summary>Its model and serial, or its serial alone.</summary>
    public string Name => Model is null ? Serial : $"{Model} ({Serial})";

    /// <summary>The entry a device picker shows.</summary>
    public BuildDevice ToBuildDevice() => new(Serial, Name);
}
