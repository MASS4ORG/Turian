using System.Runtime.InteropServices;
using System.Threading;
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Window;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using EngineApplication = Turian.Engine.Core.Application;

namespace Turian.Android;

/// <summary>Owns the Android view and forwards lifecycle changes to the engine's game thread.</summary>
[Activity(MainLauncher = true, Exported = true, Theme = "@android:style/Theme.Material.NoActionBar",
    ScreenOrientation = ScreenOrientation.FullUser,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize | ConfigChanges.Keyboard | ConfigChanges.KeyboardHidden)]
public class MainActivity : Activity, ISurfaceHolderCallback
{
    AndroidSurfaceHost? surface;
    readonly BufferedInputSource input = new();
    Thread? gameThread;
    BackCallback? back;
    bool backQuits = true;

    /// <summary>Supplies the key for encrypted OAP packages before any content is opened.</summary>
    /// <returns>A 32-byte key, or null for unencrypted content.</returns>
    protected virtual byte[]? GetContentKey() => null;

    /// <inheritdoc />
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        surface = new AndroidSurfaceHost(NativeWindow.Release);
        var view = new SurfaceView(this);
        view.Holder!.AddCallback(this);
        SetContentView(view);
        back = new BackCallback(() => surface.Post(() =>
        {
            if (backQuits)
            {
                EngineApplication.Quit();
            }
            else
            {
                input.PushKeyDown(Silk.NET.Input.Key.Escape);
                input.PushKeyUp(Silk.NET.Input.Key.Escape);
            }
        }));
        OnBackInvokedDispatcher.RegisterOnBackInvokedCallback(0, back);
        gameThread = new Thread(RunPlayer) { IsBackground = true, Name = "Turian game" };
        gameThread.Start();
    }

    /// <inheritdoc />
    protected override void OnResume()
    {
        base.OnResume();
        Window!.InsetsController?.Hide(WindowInsets.Type.SystemBars());
        if (Window.InsetsController is { } controller)
        {
            controller.SystemBarsBehavior = (int)WindowInsetsControllerBehavior.ShowTransientBarsBySwipe;
        }
        surface?.SetPaused(false);
    }

    /// <inheritdoc />
    protected override void OnPause()
    {
        surface?.Post(input.Clear);
        surface?.SetPaused(true);
        base.OnPause();
    }

    /// <inheritdoc />
    protected override void OnDestroy()
    {
        if (back is not null)
        {
            OnBackInvokedDispatcher.UnregisterOnBackInvokedCallback(back);
        }
        surface?.Close();
        gameThread?.Join();
        back?.Dispose();
        base.OnDestroy();
    }

    /// <inheritdoc />
    public void SurfaceCreated(ISurfaceHolder holder)
    {
        var native = NativeWindow.FromSurface(JNIEnv.Handle, holder.Surface!.Handle);
        var frame = holder.SurfaceFrame!;
        surface!.Attach(native, new Int2(frame.Width(), frame.Height()));
    }

    /// <inheritdoc />
    public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) =>
        surface?.Resize(new Int2(width, height));

    /// <inheritdoc />
    public void SurfaceDestroyed(ISurfaceHolder holder) => surface?.Detach();

    void RunPlayer()
    {
        string? failure = null;
        var unsupportedGraphics = false;
        try
        {
            var graphics = AndroidGraphicsSupport.Probe();
            global::Android.Util.Log.Info("Turian", $"Model={(Build.Model)}; {graphics.Diagnostics}");
            if (!graphics.IsSupported)
            {
                unsupportedGraphics = true;
                throw new NotSupportedException(graphics.Diagnostics);
            }
            using var host = Host.CreateDefaultBuilder().Build();
            var factory = host.Services.GetRequiredService<ILoggerFactory>();
            Log.Configure(factory);
            var logger = factory.CreateLogger("Turian");
            var package = PackageManager!.GetPackageInfo(PackageName!, PackageManager.PackageInfoFlags.Of(0L))!;
            var content = new ApkContentDirectory(Assets!, PackageName!, package.LongVersionCode);
            OapRuntimeKey.Set(GetContentKey());
            PrecastAssemblies.Load(content, "usercode.typeids.json", logger);
            TypeRegistry.RegisterFromManifest(content, "usercode.typeids.json", logger);
            var context = new GamePlayerContext(FilesDir!.AbsolutePath, logger,
                static services => services.BuildServiceProvider(), Content: content);
            if (content.Probe("android-player.json") is not null)
            {
                backQuits = content.ReadAllText("android-player.json").Trim() != "false";
            }
            new AndroidPlayer(surface!, input).Run(context);
        }
        catch (Exception exception)
        {
            failure = exception.Message;
            global::Android.Util.Log.Error("Turian", exception.ToString());
        }
        finally
        {
            surface!.Dispose();
            OapRuntimeKey.Set(null);
            Log.Shutdown();
            RunOnUiThread(() =>
            {
                if (failure is null)
                {
                    Finish();
                    return;
                }
                if (!IsFinishing && !IsDestroyed)
                {
                    var portuguese = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt";
                    var message = $"{Build.Model}: {failure}";
                    if (unsupportedGraphics)
                    {
                        var requirements = portuguese
                            ? "Requer Android 13, Vulkan 1.3, sincronização 2, renderização dinâmica e filtragem anisotrópica."
                            : "Requires Android 13, Vulkan 1.3, synchronization 2, dynamic rendering and anisotropic filtering.";
                        message = $"{requirements}\n{message}";
                    }
                    new AlertDialog.Builder(this).SetTitle(portuguese
                            ? "Não foi possível iniciar o Turian" : "Unable to start Turian")!
                        .SetMessage(message)!
                        .SetCancelable(false)!
                        .SetPositiveButton(global::Android.Resource.String.Ok, (_, _) => Finish())!.Show();
                }
            });
        }
    }

    sealed class BackCallback(Action callback) : Java.Lang.Object, IOnBackInvokedCallback
    {
        /// <inheritdoc />
        public void OnBackInvoked() => callback();
    }
}

static partial class NativeWindow
{
    [LibraryImport("android", EntryPoint = "ANativeWindow_fromSurface")]
    internal static partial nint FromSurface(nint environment, nint surface);

    [LibraryImport("android", EntryPoint = "ANativeWindow_release")]
    internal static partial void Release(nint window);
}
