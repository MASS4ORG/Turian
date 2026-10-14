using System.Globalization;
using Guinevere;
using Turian.Engine.UI;
using GuiColor = Guinevere.Color;

namespace Usercode;

/// <summary>Demonstrates scene capture, local recovery and conflicting device saves on three visible markers.</summary>
public sealed class SaveShowcase : Component, ISaveable<CampaignState>
{
    readonly MemorySaveStorage cloud = new();
    readonly SaveSlot slot = new("campaign");
    FileSaveStorage local = null!;
    SaveService saves = null!;
    Node player = null!;
    Node checkpoint = null!;
    Node remoteMarker = null!;
    string? localRevision;
    string? cloudRevision;
    CampaignState? localState;
    CampaignState? cloudState;
    UiDocumentComponent overlay = null!;
    string operation = "";
    bool failed;

    /// <summary>The player input, optional for headless hosts.</summary>
    [InjectService(Optional = true), JsonIgnore]
    public IInputSource? Input { get; private set; }

    /// <summary>The authoritative clock used in save metadata.</summary>
    [InjectService, JsonIgnore]
    public SimulationClock Clock { get; private set; } = null!;

    /// <summary>Local demo files; a headless host can override this before scene initialization.</summary>
    [JsonIgnore]
    public string SaveDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "turian-example-11-save");

    /// <summary>The stable authored identity of the saved player.</summary>
    [JsonIgnore]
    public Guid SaveId => player.Id;

    /// <summary>The current game score, restored together with the player position.</summary>
    [JsonIgnore]
    public int Gold { get; private set; }

    /// <summary>Whether a storage operation is in progress.</summary>
    [JsonIgnore]
    public bool Busy { get; private set; }

    /// <summary>The most recent operation result, also reported in Output.</summary>
    [JsonIgnore]
    public string Status { get; private set; } = "Ready";

    /// <summary>The number of completed operations, including reported conflicts.</summary>
    [JsonIgnore]
    public int Completed { get; private set; }

    /// <summary>The thread that applied the most recent worker result.</summary>
    [JsonIgnore]
    public int AppliedThread { get; private set; }

    /// <summary>The live player's position and score displayed in the overlay.</summary>
    [JsonIgnore]
    public string CurrentSummary => Describe(CaptureSave());

    /// <summary>The last observed local checkpoint displayed in the overlay.</summary>
    [JsonIgnore]
    public string LocalSummary => localState is null ? "empty" : Describe(localState);

    /// <summary>The last observed cloud checkpoint displayed in the overlay.</summary>
    [JsonIgnore]
    public string CloudSummary => cloudState is null ? "empty" : Describe(cloudState);

    /// <summary>The operation progress or result displayed in the overlay.</summary>
    [JsonIgnore]
    public string Feedback => Busy ? $"Working: {operation}..." : Status;

    /// <inheritdoc />
    public override void OnAwake()
    {
        player = Node!.Children.Single(static n => n.Name == "Current player");
        checkpoint = Node.Children.Single(static n => n.Name == "Local checkpoint");
        remoteMarker = Node.Children.Single(static n => n.Name == "Cloud checkpoint");
        overlay = Node.GetComponent<UiDocumentComponent>()!;
        overlay.OnBuild = BuildOverlay;
        Status = "Press Play, then click the Game view.";
    }

    /// <inheritdoc />
    public override void OnStart()
    {
        local = new FileSaveStorage(SaveDirectory);
        saves = new SaveService(local, "example:scene-campaign");
        Log.Logger.LogInformation("Save demo: A/D move; G adds gold; S saves; L loads; B loads backup; " +
            "C pushes to cloud; R simulates another device; P pulls its state. Files: {Directory}", SaveDirectory);
        Request("load");
    }

    /// <inheritdoc />
    public override void OnUpdate(float deltaTime)
    {
        if (Input is null)
        {
            return;
        }
        var direction = (Input.IsKeyDown(Key.D) ? 1 : 0) - (Input.IsKeyDown(Key.A) ? 1 : 0);
        player.Position = player.Position with { X = Math.Clamp(player.Position.X + direction * deltaTime * 2, -4, 4) };
        if (Input.WasKeyPressed(Key.G))
        {
            AddGold();
        }
        HandleRequests(Input);
    }

    void HandleRequests(IInputSource input)
    {
        if (input.WasKeyPressed(Key.S))
        {
            Request("save");
        }

        if (input.WasKeyPressed(Key.L))
        {
            Request("load");
        }

        if (input.WasKeyPressed(Key.B))
        {
            Request("backup");
        }

        if (input.WasKeyPressed(Key.C))
        {
            Request("push");
        }

        if (input.WasKeyPressed(Key.R))
        {
            Request("remote");
        }

        if (input.WasKeyPressed(Key.P))
        {
            Request("pull");
        }
    }

    /// <summary>Changes live game data without changing the saved generations.</summary>
    public void AddGold() => Gold += 10;

    /// <inheritdoc />
    public CampaignState CaptureSave() => new(SaveId, player.Position.X, Gold);

    /// <inheritdoc />
    public void RestoreSave(CampaignState state)
    {
        if (state.PlayerId != SaveId || !float.IsFinite(state.X) || state.X is < -4 or > 4 || state.Gold < 0)
        {
            throw new InvalidDataException("The saved player identity or state is invalid.");
        }
        player.Position = player.Position with { X = state.X };
        Gold = state.Gold;
    }

    /// <summary>Queues one observed operation while allowing gameplay to continue during storage work.</summary>
    public void Request(string operation)
    {
        if (Busy)
        {
            return;
        }
        Busy = true;
        this.operation = operation;
        failed = false;
        Async.Run(token => Execute(operation, token));
    }

    /// <inheritdoc />
    public override void OnDestroy() => overlay.OnBuild = null;

    static string Describe(CampaignState state) =>
        string.Create(CultureInfo.InvariantCulture, $"X {state.X:0.00}   Gold {state.Gold}");

    void BuildOverlay(Gui gui)
    {
        using (gui.Node(520, 374, "save-demo-hud").Absolute(16, 16).Padding(16).Gap(7)
                   .Direction(Axis.Vertical).Enter())
        {
            if (gui.Pass == Pass.Pass2Render)
            {
                gui.DrawRect(gui.CurrentNode.Rect, GuiColor.FromArgb(238, 16, 23, 35), 10);
            }
            Label(gui, "GAME SAVES", GuiColor.White, 23);
            Label(gui, "A / D move    G adds gold", GuiColor.White);
            Label(gui, "S save    L load    B restore backup", GuiColor.White);
            Label(gui, "C push    R edit remote    P choose cloud", GuiColor.White);
            Label(gui, $"Live player       {CurrentSummary}", GuiColor.FromArgb(110, 218, 248));
            Label(gui, $"Local checkpoint  {LocalSummary}", GuiColor.FromArgb(174, 222, 134));
            Label(gui, $"Cloud checkpoint  {CloudSummary}", GuiColor.FromArgb(218, 173, 240));
            Label(gui, Feedback, failed ? GuiColor.FromArgb(255, 148, 119) : GuiColor.FromArgb(255, 222, 136));
            Label(gui, "Backup: save twice, change state, then press B.", GuiColor.White, 14);
            Label(gui, "Cloud conflict: S, C, R, C. Press P to choose cloud.", GuiColor.White, 14);
            Label(gui, "Click the Game view for gameplay input.", GuiColor.White, 14);
        }
    }

    static void Label(Gui gui, string text, GuiColor color, float size = 16)
    {
        using (gui.Node(-1, 24).ExpandWidth().Enter())
        {
            gui.DrawText(text, size, color, centerInRect: false, clip: true);
        }
    }

    async ValueTask Execute(string operation, CancellationToken token)
    {
        var origin = Async;
        var snapshot = CaptureSave();
        var metadata = new SaveMetadata { Tick = Clock.TickIndex, GameVersion = "0.1.0" };
        Outcome outcome;
        var errorResult = false;
        try
        {
            outcome = await Persist(operation, snapshot, metadata, token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            outcome = new Outcome(error.Message, localRevision, cloudRevision);
            errorResult = true;
        }
        await origin.MainThread(token);
        if (outcome.Restore is not null)
        {
            RestoreSave(outcome.Restore);
        }
        if (outcome.Local is not null)
        {
            localState = outcome.Local;
            checkpoint.Position = checkpoint.Position with { X = outcome.Local.X };
        }
        if (outcome.Cloud is not null)
        {
            cloudState = outcome.Cloud;
            remoteMarker.Position = remoteMarker.Position with { X = outcome.Cloud.X };
        }
        localRevision = outcome.LocalRevision;
        cloudRevision = outcome.CloudRevision;
        Status = outcome.Message;
        failed = errorResult;
        AppliedThread = Environment.CurrentManagedThreadId;
        Completed++;
        Busy = false;
        Log.Logger.LogInformation("Save demo: {Status}", Status);
    }

    async Task<Outcome> Persist(string operation, CampaignState snapshot, SaveMetadata metadata,
        CancellationToken token)
    {
        switch (operation)
        {
            case "save":
                var written = await saves.SaveAsync(slot, snapshot, metadata, localRevision, token)
                    .ConfigureAwait(false);
                return new Outcome("Saved local checkpoint", written, cloudRevision, Local: snapshot);
            case "load":
                var loaded = await saves.LoadAsync<CampaignState>(slot, cancellationToken: token).ConfigureAwait(false);
                return loaded is null ? new Outcome("Local slot is empty", null, cloudRevision)
                    : new Outcome("Loaded local checkpoint", loaded.Revision, cloudRevision,
                        loaded.State, loaded.State);
            case "backup":
                var backup = await saves.LoadBackupAsync<CampaignState>(slot, cancellationToken: token)
                    .ConfigureAwait(false);
                return new Outcome(backup is null ? "Backup is empty" : "Restored previous generation",
                    localRevision, cloudRevision, backup?.State);
            case "push":
                var pushed = await SaveTransfer.CopyAsync(local, cloud, slot,
                    localRevision ?? throw new InvalidOperationException("Save locally before pushing"),
                    cloudRevision, token).ConfigureAwait(false);
                var synced = await new SaveService(cloud, "example:scene-campaign")
                    .LoadAsync<CampaignState>(slot, cancellationToken: token).ConfigureAwait(false);
                return new Outcome("Pushed local checkpoint", localRevision, pushed, Cloud: synced!.State);
            case "remote":
                var remote = new SaveService(cloud, "example:scene-campaign");
                var observed = await remote.LoadAsync<CampaignState>(slot, cancellationToken: token)
                    .ConfigureAwait(false);
                var changed = snapshot with { X = Math.Clamp(snapshot.X + 1, -4, 4), Gold = snapshot.Gold + 100 };
                await remote.SaveAsync(slot, changed, metadata, observed?.Revision, token).ConfigureAwait(false);
                return new Outcome("Another device changed cloud; the next push must conflict",
                    localRevision, cloudRevision, Cloud: changed);
            case "pull":
                var current = await cloud.ReadAsync(slot, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Cloud slot is empty");
                var pulled = await SaveTransfer.CopyAsync(cloud, local, slot, current.Revision, localRevision, token)
                    .ConfigureAwait(false);
                var state = (await saves.LoadAsync<CampaignState>(slot, cancellationToken: token)
                    .ConfigureAwait(false))!.State;
                return new Outcome("Chose cloud checkpoint and restored it",
                    pulled, current.Revision, state, state, state);
            default:
                throw new InvalidOperationException("Unknown demo operation");
        }
    }

    sealed record Outcome(string Message, string? LocalRevision, string? CloudRevision,
        CampaignState? Restore = null, CampaignState? Local = null, CampaignState? Cloud = null);
}

/// <summary>Game-owned state with a persistent entity identifier and no live scene references.</summary>
/// <param name="PlayerId">The authored identity of the player node.</param>
/// <param name="X">The captured horizontal position.</param>
/// <param name="Gold">The captured game score.</param>
public sealed record CampaignState(Guid PlayerId, float X, int Gold);
