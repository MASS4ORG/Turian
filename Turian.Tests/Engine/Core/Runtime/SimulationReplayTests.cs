namespace Turian.Tests;

/// <summary>Headless replay proof for a supported integer-only world and explicit state hash selection.</summary>
public sealed class SimulationReplayTests
{
    static readonly Guid Producer = Guid.Parse("7ace9fd6-23ad-4616-bc23-114a19e32b3f");
    static SimulationSession Create(SimulationTestWorld? world = null) =>
        new(world ?? new SimulationTestWorld(), new SimulationClock(), 9876, "integer-fixture-v1");

    static SimulationCommand[] Record(SimulationSession session)
    {
        return [.. new long[] { 2, 1, 90, 120 }.Select((tick, index) => session.Schedule(Producer, index + 1, "add",
            JsonSerializer.SerializeToElement(new { amount = (index + 1) * 100 }), targetTick: tick).Command!)];
    }

    /// <summary>Recorded authority decisions replay to identical full-state hashes at 30, 60 and 144 rendering Hz.</summary>
    [Theory]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(144)]
    public void Replay_MatchesFullCheckpointHashAcrossRenderingSchedules(int rate)
    {
        var reference = Create();
        var expectedHashes = new List<string>();
        reference.TickCompleted += session => expectedHashes.Add(session.ComputeStateHash());
        var log = Record(reference);
        for (var frame = 0; frame < 120; frame++) reference.Advance(1d / 60);
        var replay = Create();
        var actualHashes = new List<string>();
        replay.TickCompleted += session => actualHashes.Add(session.ComputeStateHash());
        var json = JsonSerializer.Serialize(log);
        foreach (var command in JsonSerializer.Deserialize<SimulationCommand[]>(json)!)
            Assert.Equal(SimulationCommandStatus.Accepted, replay.ScheduleRecorded(command).Status);
        for (var frame = 0; frame < rate * 2; frame++) replay.Advance(1d / rate);
        Assert.Equal(120, replay.Clock.TickIndex);
        Assert.Equal(reference.ComputeStateHash(), replay.ComputeStateHash());
        Assert.Equal(120, expectedHashes.Count);
        Assert.Equal(expectedHashes, actualHashes);
        Assert.Equal(reference.CaptureState().World.GetRawText(), replay.CaptureState().World.GetRawText());
    }

    /// <summary>RuntimeSave resumes pending commands, RNG streams and producer cursors under irregular rendering.</summary>
    [Fact]
    public async Task SaveResume_MatchesReplayAndRetainsPendingCommandsAndDeduplication()
    {
        var first = Create();
        Record(first);
        for (var frame = 0; frame < 60; frame++) first.Advance(1d / 60);
        var save = first.CaptureSave();
        var content = new RuntimeSaveContent { Fingerprint = "integer-fixture-content-v1" };
        var codec = new RuntimeSave();
        var text = codec.Write(save, content);
        var loader = Substitute.For<IAssetLoader>();
        await Assert.ThrowsAsync<InvalidDataException>(() => codec.ReadAsync(text, content, loader));
        var loaded = await codec.ReadAsync(text, content, loader,
            new HashSet<string> { SimulationCheckpoint.ExtensionName });
        var resumed = Create();
        resumed.RestoreSave(loaded.Snapshot);
        Assert.Equal(first.ComputeStateHash(), resumed.ComputeStateHash());
        Assert.Equal(2, resumed.CaptureState().Commands.Pending.Length);
        Assert.Equal(SimulationCommandStatus.DuplicateOrStale,
            resumed.Schedule(Producer, 4, "add", JsonSerializer.SerializeToElement(new { amount = 999 })).Status);
        for (var frame = 0; frame < 20; frame++)
        {
            resumed.Advance(0.003);
            resumed.Advance(0.047);
        }
        for (var frame = 0; frame < 60; frame++) first.Advance(1d / 60);
        Assert.Equal(120, resumed.Clock.TickIndex);
        Assert.Equal(first.ComputeStateHash(), resumed.ComputeStateHash());
        Assert.Throws<InvalidOperationException>(() => resumed.RestoreSave(loaded.Snapshot));
    }

    /// <summary>Malformed metadata, cursors and pending commands are rejected before world or clock mutation.</summary>
    [Fact]
    public void Restore_RejectsIncompatibleMetadataBeforeMutation()
    {
        var source = Create();
        Record(source);
        source.Step();
        var state = source.CaptureState();
        var command = state.Commands.Pending[0];
        var cursor = state.Commands.Producers[0];
        var mutations = new Func<SimulationCheckpoint, SimulationCheckpoint>[]
        {
            value => value with { Version = 9 },
            value => value with { Compatibility = "different" },
            value => value with { World = default },
            value => value with { Clock = null! },
            value => value with { Random = null! },
            value => value with { Commands = null! },
            value => value with { Clock = value.Clock with { FixedDeltaTime = 0.1 } },
            value => value with { Random = value.Random with { Version = 2 } },
            value => value with { Random = value.Random with { Seed = 1 } },
            value => value with { Random = value.Random with { Streams = [] } },
            value => value with { Random = value.Random with { Streams = null! } },
            value => value with { Commands = value.Commands with { LastAuthoritySequence = -1 } },
            value => value with { Commands = value.Commands with { Pending = null! } },
            value => value with { Commands = value.Commands with { Producers = null! } },
            value => value with { Commands = value.Commands with { Producers = [null!] } },
            value => value with { Commands = value.Commands with { Producers = [cursor, cursor] } },
            value => value with { Commands = value.Commands with { Producers = [cursor with { Producer = Guid.Empty }] } },
            value => value with { Commands = value.Commands with { Producers = [cursor with { Sequence = 0 }] } },
            value => value with { Commands = value.Commands with { Pending = [null!] } },
            value => value with { Commands = value.Commands with { Pending = [command, command] } },
            value => value with { Commands = value.Commands with { Pending = [command with { TargetTick = 1 }] } },
            value => value with { Commands = value.Commands with { Pending = [command with { AuthoritySequence = 99 }] } },
            value => value with { Commands = value.Commands with { Pending = [command with { Producer = Guid.NewGuid() }] } },
            value => value with { Commands = value.Commands with { Pending = [command with { ProducerSequence = 99 }] } },
            value => value with { Commands = value.Commands with { Pending = [command, command with
                { AuthoritySequence = 2 }] } }
        };
        foreach (var mutate in mutations)
        {
            var fresh = Create();
            var before = fresh.ComputeStateHash();
            Assert.Throws<InvalidDataException>(() => fresh.RestoreState(mutate(state)));
            Assert.Equal(before, fresh.ComputeStateHash());
        }
        Assert.Throws<ArgumentNullException>(() => Create().RestoreState(null!));
        var restored = Create();
        restored.RestoreState(state);
        Assert.Equal(source.ComputeStateHash(), restored.ComputeStateHash());
        Assert.Throws<InvalidOperationException>(() => restored.RestoreState(state));
        var admitted = Create();
        Record(admitted);
        Assert.Throws<InvalidOperationException>(() => admitted.RestoreState(state));
    }

    /// <summary>Corrupt save mirrors cannot substitute another tick, RNG stream or game state.</summary>
    [Fact]
    public void RuntimeSave_RejectsMissingExtensionsAndInconsistentMirrors()
    {
        var source = Create();
        source.Step();
        var snapshot = source.CaptureSave();
        Assert.Throws<InvalidDataException>(() => Create().RestoreSave(new RuntimeSaveSnapshot()));
        Assert.Throws<ArgumentNullException>(() => Create().RestoreSave(null!));
        var extension = snapshot.Extensions[SimulationCheckpoint.ExtensionName];
        extension.Required = false;
        Assert.Throws<InvalidDataException>(() => Create().RestoreSave(snapshot));
        extension.Required = true;
        var data = extension.Data;
        extension.Data = null;
        Assert.Throws<InvalidDataException>(() => Create().RestoreSave(snapshot));
        extension.Data = data;
        snapshot.Tick++;
        Assert.Throws<InvalidDataException>(() => Create().RestoreSave(snapshot));
        snapshot.Tick--;
        snapshot.RngState++;
        Assert.Throws<InvalidDataException>(() => Create().RestoreSave(snapshot));
        snapshot.RngState--;
        snapshot.State["Balance"] = -1;
        Assert.Throws<InvalidDataException>(() => Create().RestoreSave(snapshot));
    }

    /// <summary>A failed game restore faults the session, preventing capture of partially restored state.</summary>
    [Fact]
    public void GameRestoreFailure_FaultsTheSession()
    {
        var source = Create();
        source.Step();
        var target = Create(new SimulationTestWorld { FailRestore = true });
        Assert.Throws<InvalidOperationException>(() => target.RestoreState(source.CaptureState()));
        Assert.Throws<InvalidOperationException>(() => target.CaptureState());
        Assert.Throws<InvalidOperationException>(() => target.Step());
    }

    /// <summary>Hash input canonicalizes nested object keys while preserving arrays and numbers.</summary>
    [Fact]
    public void Hash_CanonicalizesObjectKeysAndRejectsDuplicateKeys()
    {
        using var first = JsonDocument.Parse("{\"b\":[{\"y\":2,\"x\":1},true,null],\"a\":\"text\"}");
        using var second = JsonDocument.Parse("{ \"a\":\"text\",\"b\":[{\"x\":1,\"y\":2},true,null] }");
        Assert.Equal(SimulationStateHash.Compute(first.RootElement), SimulationStateHash.Compute(second.RootElement));
        using var duplicates = JsonDocument.Parse("{\"x\":1,\"x\":2}");
        Assert.Throws<InvalidDataException>(() => SimulationStateHash.Compute(duplicates.RootElement));
        using var number = JsonDocument.Parse("1");
        using var decimalNumber = JsonDocument.Parse("1.0");
        Assert.NotEqual(SimulationStateHash.Compute(number.RootElement), SimulationStateHash.Compute(decimalNumber.RootElement));
        Assert.Throws<InvalidOperationException>(() => SimulationStateHash.Compute(default));
    }
}
