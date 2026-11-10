namespace Turian.Tests;

/// <summary>Points the Android user settings at an SDK and a JDK made of empty folders, and restores them after.</summary>
static class AndroidFakeTools
{
    /// <summary>Creates the folders under <paramref name="root"/> and selects them.</summary>
    public static IDisposable Use(string root)
    {
        var settings = AndroidBuildPlatform.Settings;
        var (sdk, jdk) = (settings.SdkDirectory, settings.JdkDirectory);
        settings.SdkDirectory = Sdk(Path.Combine(root, "sdk"));
        settings.JdkDirectory = Jdk(Path.Combine(root, "jdk"), "17.0.12");
        return new Restore(() => (settings.SdkDirectory, settings.JdkDirectory) = (sdk, jdk));
    }

    /// <summary>An SDK folder with platform tools.</summary>
    public static string Sdk(string folder)
    {
        Directory.CreateDirectory(Path.Combine(folder, "platform-tools"));
        return folder;
    }

    /// <summary>A JDK folder with a compiler and the version its release file names.</summary>
    public static string Jdk(string folder, string? version)
    {
        Directory.CreateDirectory(Path.Combine(folder, "bin"));
        File.WriteAllText(Path.Combine(folder, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac"), "");
        if (version is not null)
        {
            File.WriteAllText(Path.Combine(folder, "release"), $"IMPLEMENTOR=\"Test\"\nJAVA_VERSION=\"{version}\"\n");
        }

        return folder;
    }

    sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}

/// <summary>Finding the Android tools, choosing a device and reading the user's choices, without any installed.</summary>
[Collection(SerialTests.Name)]
public sealed class AndroidToolchainTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("turian-android-tools-").FullName;

    /// <inheritdoc/>
    public void Dispose() => Directory.Delete(root, recursive: true);

    static Func<string, string?> Variables(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(value => value.Name == name).Value;

    /// <summary>The configured SDK wins, then ANDROID_HOME, ANDROID_SDK_ROOT and the usual folders; empty folders are skipped.</summary>
    [Fact]
    public void SdkSearchFollowsTheUsersChoiceThenTheEnvironmentThenTheUsualFolders()
    {
        var configured = AndroidFakeTools.Sdk(Path.Combine(root, "configured"));
        var home = AndroidFakeTools.Sdk(Path.Combine(root, "home"));
        var sdkRoot = AndroidFakeTools.Sdk(Path.Combine(root, "sdk-root"));
        var usual = AndroidFakeTools.Sdk(Path.Combine(root, "usual"));
        var empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        var environment = Variables(("ANDROID_HOME", home), ("ANDROID_SDK_ROOT", sdkRoot));

        Assert.Equal(configured, AndroidToolchain.FindSdk(configured, environment, [usual]));
        Assert.Equal(home, AndroidToolchain.FindSdk("", environment, [usual]));
        Assert.Equal(home, AndroidToolchain.FindSdk(empty, environment, [usual]));
        Assert.Equal(sdkRoot, AndroidToolchain.FindSdk(null, Variables(("ANDROID_SDK_ROOT", sdkRoot)), [usual]));
        Assert.Equal(usual, AndroidToolchain.FindSdk(null, Variables(), [empty, usual]));
        Assert.Null(AndroidToolchain.FindSdk(null, Variables(), [empty]));
    }

    /// <summary>A JDK needs a compiler and, when its release file names one, version 17 or newer.</summary>
    [Fact]
    public void JdkSearchSkipsFoldersWithoutACompilerAndOldVersions()
    {
        var old = AndroidFakeTools.Jdk(Path.Combine(root, "jdk-11"), "11.0.2");
        var legacy = AndroidFakeTools.Jdk(Path.Combine(root, "jdk-8"), "1.8.0_402");
        var unknown = AndroidFakeTools.Jdk(Path.Combine(root, "jdk-unknown"), null);
        var current = AndroidFakeTools.Jdk(Path.Combine(root, "jdk-21"), "21.0.4");
        var runtimeOnly = Directory.CreateDirectory(Path.Combine(root, "jre", "bin")).Parent!.FullName;

        Assert.Equal(11, AndroidToolchain.JdkVersion(old));
        Assert.Equal(8, AndroidToolchain.JdkVersion(legacy));
        Assert.Equal(current, AndroidToolchain.FindJdk(old, Variables(("JAVA_HOME", legacy)), [runtimeOnly, current]));
        Assert.Equal(unknown, AndroidToolchain.FindJdk(null, Variables(("JAVA_HOME", unknown)), [current]));
        Assert.Null(AndroidToolchain.FindJdk(null, Variables(), [old, runtimeOnly]));
    }

    /// <summary>Every device keeps its state; only ready ones become picker entries, named by model and serial.</summary>
    [Fact]
    public void DeviceListingKeepsStatesAndNamesReadyDevices()
    {
        const string listing = """
            * daemon not running; starting now at tcp:5037
            * daemon started successfully
            List of devices attached
            R5CT123ABC             device usb:1-1 product:x1q model:SM_S721B device:x1q transport_id:3
            emulator-5554          device product:sdk_gphone64 model:sdk_gphone64_x86_64 transport_id:1
            0123456789ABCDEF       unauthorized usb:1-2 transport_id:4
            192.168.0.5:5555       offline
            """;

        var devices = AndroidToolchain.ParseDevices(listing);

        Assert.Equal(
            [new AdbDevice("R5CT123ABC", "device", "SM S721B"),
                new AdbDevice("emulator-5554", "device", "sdk gphone64 x86 64"),
                new AdbDevice("0123456789ABCDEF", "unauthorized", null),
                new AdbDevice("192.168.0.5:5555", "offline", null)],
            devices);
        Assert.Equal(new BuildDevice("R5CT123ABC", "SM S721B (R5CT123ABC)"), devices[0].ToBuildDevice());
        Assert.Empty(AndroidToolchain.ParseDevices("List of devices attached\n\n"));
    }

    /// <summary>
    /// The requested device must be ready; otherwise the only ready one is used. None, several, unauthorized and
    /// offline devices each fail with what to do.
    /// </summary>
    [Fact]
    public void DeviceChoiceIsRequestedOrUnambiguous()
    {
        AdbDevice phone = new("phone", "device", "Phone"), tablet = new("tablet", "device", "Tablet"),
            locked = new("locked", "unauthorized", null), gone = new("gone", "offline", null);

        Assert.Equal("tablet", AndroidToolchain.ChooseDevice([phone, tablet], "tablet"));
        Assert.Equal("phone", AndroidToolchain.ChooseDevice([phone, locked, gone], null));
        Assert.Equal("phone", AndroidToolchain.ChooseDevice([phone], " "));
        Assert.Contains("Tablet", Fails([phone, tablet], null));
        Assert.Contains("USB debugging on", Fails([gone], null));
        Assert.Contains("accept the USB debugging prompt", Fails([locked, gone], null));
        Assert.Contains("accept the USB debugging prompt", Fails([phone, locked], "locked"));
        Assert.Contains("offline", Fails([phone, gone], "gone"));
        Assert.Contains("watch", Fails([phone], "watch"));

        static string Fails(IReadOnlyList<AdbDevice> devices, string? requested) =>
            Assert.Throws<InvalidOperationException>(() => AndroidToolchain.ChooseDevice(devices, requested)).Message;
    }

    /// <summary>Installation failures name the adb code and what to do; a success reports none.</summary>
    [Theory]
    [InlineData("Performing Streamed Install\nSuccess\n", null)]
    [InlineData("Performing Streamed Install\nadb: failed to install app.apk: Failure [INSTALL_FAILED_UPDATE_INCOMPATIBLE: Existing package org.example.app signatures do not match newer version; ignoring!]",
        "uninstall it")]
    [InlineData("Failure [INSTALL_FAILED_VERSION_DOWNGRADE]", "version code")]
    [InlineData("Failure [INSTALL_FAILED_NO_MATCHING_ABIS: Failed to extract native libraries, res=-113]", "architecture")]
    [InlineData("Failure [INSTALL_FAILED_USER_RESTRICTED: Install canceled by user]", "allow installing over USB")]
    [InlineData("Failure [INSTALL_FAILED_SOMETHING_NEW]", "INSTALL_FAILED_SOMETHING_NEW")]
    public void InstallFailuresSayWhatToDo(string output, string? expected)
    {
        var failure = AndroidToolchain.InstallFailure(output);

        if (expected is null)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Contains(expected, failure);
        }
    }

    /// <summary>The launcher component, the start report and the process id are read from the device's answers.</summary>
    [Fact]
    public void StartAnswersAreParsed()
    {
        const string resolved = """
            priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=false
            org.example.app/crc64a1b2c3d4e5f6.MainActivity
            """;
        Assert.Equal("org.example.app/crc64a1b2c3d4e5f6.MainActivity",
            AndroidToolchain.LauncherComponent(resolved, "org.example.app"));
        Assert.Null(AndroidToolchain.LauncherComponent("No activity found", "org.example.app"));

        const string started = """
            Starting: Intent { cmp=org.example.app/crc64a1b2c3d4e5f6.MainActivity }
            Status: ok
            LaunchState: COLD
            Activity: org.example.app/crc64a1b2c3d4e5f6.MainActivity
            TotalTime: 302
            WaitTime: 310
            Complete
            """;
        Assert.Equal((true, 302, null), AndroidToolchain.ParseStart(started));
        Assert.False(AndroidToolchain.ParseStart("Starting: Intent { }\nError type 3\nError: Activity class does not exist.").Ok);
        Assert.Equal((false, null, "status timeout"), AndroidToolchain.ParseStart("Status: timeout\nComplete"));

        Assert.Equal(23860, AndroidToolchain.ParsePid("23860\n"));
        Assert.Equal(23860, AndroidToolchain.ParsePid("23860 23861"));
        Assert.Null(AndroidToolchain.ParsePid(""));
    }

    /// <summary>The Android build is told where the tools are, so it does not depend on how the editor started.</summary>
    [Fact]
    public void PublishingNamesTheFoundTools()
    {
        using var tools = AndroidFakeTools.Use(root);
        var context = new BuildPlatformContext(new BuildAppSettings { ProjectAbsoluteDir = root },
            NullLogger.Instance, Path.Combine(root, "app.csproj"), root, "Release", null);

        var arguments = new AndroidBuildPlatform().PublishArguments(context);

        Assert.Contains($"-p:AndroidSdkDirectory={AndroidBuildPlatform.Settings.SdkDirectory}", arguments);
        Assert.Contains($"-p:JavaSdkDirectory={AndroidBuildPlatform.Settings.JdkDirectory}", arguments);
    }

    /// <summary>Android installs on devices and follows the user's choice to do so after every export; desktop does neither.</summary>
    [Fact]
    public void DeploymentFollowsThePlatformAndTheUsersChoice()
    {
        IBuildPlatform android = new AndroidBuildPlatform(), desktop = BuildPlatforms.Find(null);
        var settings = AndroidBuildPlatform.Settings;

        Assert.True(android.Deploys);
        Assert.Same(settings, android.UserSettings);
        Assert.False(desktop.Deploys);
        Assert.Null(desktop.UserSettings);
        Assert.Null(desktop.ConfiguredDeployment);
        try
        {
            settings.InstallOnDevice = false;
            Assert.Null(android.ConfiguredDeployment);
            settings.InstallOnDevice = true;
            Assert.Equal(new BuildDeployment(null), android.ConfiguredDeployment);
        }
        finally
        {
            settings.InstallOnDevice = false;
        }
    }

    /// <summary>A tool without Studio reads the choices Studio saved; missing or malformed values keep the defaults.</summary>
    [Fact]
    public void UserSettingsAreReadFromTheFileStudioWrites()
    {
        var file = Path.Combine(root, "settings.json");
        File.WriteAllText(file, """
            {
              "gaya.appearance": { "Theme": "gaya.dark" },
              "turian.android": { "SdkDirectory": "/opt/sdk", "InstallOnDevice": true, "Device": 42 }
            }
            """);

        var page = UserSettingsFile.Load(new AndroidToolchainSettings(), file);

        Assert.Equal("/opt/sdk", page.SdkDirectory);
        Assert.True(page.InstallOnDevice);
        Assert.Equal("", page.Device);
        Assert.Equal("", page.JdkDirectory);
        Assert.Equal("", UserSettingsFile.Load(new AndroidToolchainSettings(), Path.Combine(root, "none.json")).SdkDirectory);
        File.WriteAllText(file, "{ not json");
        Assert.Equal("", UserSettingsFile.Load(new AndroidToolchainSettings(), file).SdkDirectory);
    }
}
