using System.Diagnostics;
using System.Text.Json;
using Turian.Engine.UI;
using Usercode;

var directory = Path.Combine(Path.GetTempPath(), "turian-save-scene-" + Guid.NewGuid().ToString("N"));
using var scheduler = new GameLoopScheduler { CaptureCreationStacks = false };
var clock = new SimulationClock();
var input = new BufferedInputSource();
var services = new SceneServices(scheduler, clock, input);
using var metadata = JsonDocument.Parse(
    File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "SaveShowcase.cs.meta")));
TypeRegistry.Register(metadata.RootElement.GetProperty("Id").GetGuid(), typeof(SaveShowcase));
TypeRegistry.ScanAssembly(typeof(UiDocumentComponent).Assembly);
var root = Serializer.LoadData<Node>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "save.prefab")))!;
var demo = root.GetComponent<SaveShowcase>()!;
demo.SaveDirectory = directory;
root.Awake(null, services, allowMissingServices: true);
var scenes = new SceneManager(new AssetDatabase());
scenes.AdoptScene(Guid.NewGuid(), root);
var ticker = new SceneTicker(scenes, clock) { Async = scheduler, InputSource = input };

try
{
    ticker.Tick(1d / 60);
    Wait();
    Require(demo.Status == "Local slot is empty", "scene starts with an empty slot");
    Require(root.GetComponent<UiDocumentComponent>()!.OnBuild is not null, "GUI brick overlay is bound");
    Require(demo.LocalSummary == "empty" && demo.CloudSummary == "empty", "empty checkpoints are visible");
    var startX = demo.CaptureSave().X;
    input.PushKeyDown(Key.D);
    ticker.Tick(0.2);
    input.PushKeyUp(Key.D);
    Require(demo.CaptureSave().X > startX, "D moves the player right");
    input.PushKeyDown(Key.A);
    ticker.Tick(0.2);
    input.PushKeyUp(Key.A);
    Require(Math.Abs(demo.CaptureSave().X - startX) < 0.01, "A moves the player left");
    input.PushKeyDown(Key.Right);
    ticker.Tick(0.2);
    input.PushKeyUp(Key.Right);
    Require(Math.Abs(demo.CaptureSave().X - startX) < 0.01, "arrow keys do not move the player");
    Press(Key.G);
    Press(Key.S);
    Require(demo.Gold == 10 && demo.Status == "Saved local checkpoint", "scene saves live DTO");
    Require(demo.LocalSummary.Contains("Gold 10", StringComparison.Ordinal), "HUD shows the local checkpoint score");
    Press(Key.G);
    Press(Key.S);
    Press(Key.G);
    Press(Key.B);
    Require(demo.Gold == 10, "backup restores previous generation");
    Press(Key.L);
    Require(demo.Gold == 20, "load restores primary generation");
    Press(Key.C);
    Require(demo.Status == "Pushed local checkpoint", "cloud push");
    Press(Key.R);
    Press(Key.C);
    Require(demo.Status.Contains("changed", StringComparison.Ordinal), "cloud conflict is reported");
    Require(demo.Feedback.Contains("changed", StringComparison.Ordinal), "HUD reports the cloud conflict");
    Require(demo.CloudSummary.Contains("Gold 120", StringComparison.Ordinal), "HUD retains the remote score");
    Require(demo.Gold == 20, "conflict preserves live state");
    Press(Key.P);
    Require(demo.Gold == 120, "explicit cloud choice restores remote score");
    Require(demo.AppliedThread == Environment.CurrentManagedThreadId, "restore runs on simulation thread");
    var snapshot = demo.CaptureSave();
    Require(snapshot.PlayerId == root.Children.Single(static n => n.Name == "Current player").Id,
        "scene identity survives capture");
    Require(root.Children.Single(static n => n.Name == "Current player").Position.X == snapshot.X,
        "scene marker reflects restored position");
    Console.WriteLine("SAVE SCENE SMOKE PASS");
}
finally
{
    scheduler.Dispose();
    scenes.UnloadAllScenes();
    Directory.Delete(directory, true);
}

void Press(Key key)
{
    input.PushKeyDown(key);
    ticker.Tick(1d / 60);
    input.PushKeyUp(key);
    Wait();
}

void Wait()
{
    var deadline = Stopwatch.StartNew();
    while (demo.Busy && deadline.Elapsed < TimeSpan.FromSeconds(10))
    {
        ticker.Tick(1d / 60);
        Thread.Sleep(1);
    }
    Require(!demo.Busy, "scene operation completed");
}

static void Require(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException("FAIL: " + message);
    }
    Console.WriteLine("PASS: " + message);
}

/// <summary>Supplies the same managed clock, scheduler and input contracts used by Play Mode.</summary>
sealed class SceneServices(GameLoopScheduler scheduler, SimulationClock clock, IInputSource input) : IServiceProvider
{
    /// <inheritdoc />
    public object? GetService(Type type) => type == typeof(GameLoopScheduler) ? scheduler
        : type == typeof(SimulationClock) ? clock : type == typeof(IInputSource) ? input : null;
}
