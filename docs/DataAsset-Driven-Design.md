# DataAsset-Driven Design

Gameplay communicates through designer-created DataAssets. Publishers and listeners reference the same GUID; neither needs a component reference to the other. Engine Core retains the session, dispatch, hydration and generated component-lifecycle contracts. The independent `org.mass4.turian.ddd` brick supplies the asset families, listeners, responses and subsystem wrappers; `org.mass4.turian.ddd.studio` supplies authoring menus and diagnostics. Both brick repositories live under `/mass4/dev/Bricks/` and keep their own behavior tests.

## Assets and ownership

`DataAssetEvent` is a signal. `DataAssetEvent<T>` carries a typed payload; prefer `readonly record struct` payloads to keep raises allocation-free. `DataAssetVariable<T>` separates `DefaultValue` from its session-owned `Value`, and offers typed `DataAssetValueChange<T>` subscriptions plus an optional `OnChanged` event asset. Float, Int, Bool and AssetEnum concrete variables are available without game code. `DataAssetConst<T>` rejects value changes while hydrated. `DataAssetRuntimeSet<T>` stores insertion-ordered live membership in its session.

The runtime loader hydrates each GUID once per loader, resolves asset references, and attaches gameplay assets to a `DataAssetSession`. Editor definitions do not acquire gameplay state. Play and standalone players create separate sessions; disposing a session removes subscriptions, clears queued payloads and runtime values, and detaches retained asset instances. Subscribe handles belong to components: dispose on disable or destruction. `DataAssetListenerComponent` does this automatically for its response assets.

Every DataAsset writes `SchemaVersion`. Version-zero payloads pass through the virtual migration hook; unsupported future versions fail before runtime hydration. Preserve asset and type GUIDs through renames. Signed packages and backups must contain the schema-bearing payload; migrations happen on loaded content, without rewriting the signed source.

## Dispatch contract

Raise, subscribe, dispose and variable mutation run on the session's game-loop thread. `TryQueue(payload)` is the worker callback entry point: its bounded, preallocated typed queue returns false on overflow. Physics/Bepu code must handle that result explicitly. SceneTicker drains queues after fixed-tick systems and before completion notifications. Queued events from different topics drain in hydration order; within a topic, enqueue-lock acquisition defines arrival order. Cross-worker arrival order is not a deterministic simulation input.

Higher listener priorities run first; equal priorities retain subscription order. Removing a listener suppresses its remaining delivery immediately. Listeners added during delivery join subsequent raises, including nested raises. Exceptions propagate and stop the current raise; pooled snapshots are still released. The broker never invokes methods by name, uses reflection, boxes struct payloads, or allocates a listener snapshot during an ordinary raise. Event tracing receives a compact record once per raise.

These are gameplay-tier events. Fixed updates, per-contact processing, particle updates and other frequent engine callbacks remain direct subsystem calls.

## Generated wiring

```csharp
[DataAssetEventDefinition("DamageEvent", "adf29e7d-d7d5-410b-a9e8-1e1e571f9a5e")]
public readonly record struct Damage(int Amount);

public partial class HealthComponent : Component
{
    public DataAssetEvent<Damage>? Damage { get; set; }

    [DataAssetListen(nameof(Damage), priority: 10)]
    void Receive(Damage damage) { /* compiled gameplay code */ }
}
```

The event generator emits the closed asset type and its serializer. The wiring generator emits typed method-group subscriptions and their disposal, invoked by the component lifecycle. The member name is resolved at compile time; no name is dispatched at runtime. Mismatched payloads or unsupported component shapes are compiler errors.

## Studio and UI

The asset browser's DataAsset menu creates signals, variables, constants, runtime sets, enum values, input readers, transitions and responses. View → DataAsset Events shows hydrated values, permits scalar edits and signal raises, lists listener owners/priorities, and retains a bounded event timeline. Its validation action reports missing targets, mismatched types and serialized members removed during refactors. It is a development tool; typed payload editing and project-wide automatic validation on every recompile remain future extensions.

The UI brick's `ButtonComponent.OnClick` is a designer-assigned `List<DataAssetEvent>`. Its `Element` names a button in the document on the same node; a click calls `Click()` directly. `UiDocumentComponent.DataAssets` lists the GUID-bound values/events available to that document. A label such as `<Label text="{asset://VARIABLE-GUID}" />` reads the current value; `click="asset://EVENT-GUID"` raises a signal directly. GUIDs identify assets, not method names. Existing controller internals remain behind their legacy adapter; DataAsset wiring does not invoke them.

`InputReader` exposes a selected action's value and press/release event lists; `InputReaderComponent` forwards gameplay edges from the project's loaded input maps. `DataAssetStateMachineComponent` uses asset-enum states and designer-authored `DataAssetStateTransition` assets, taking at most one transition per trigger raise. The existing tag/layer core is unchanged.

## Reproducing the infrastructure spike

```sh
dotnet run -c Release --project tools/DataAssetSpike
dotnet publish tools/DataAssetSpike -c Release -r linux-x64 -p:PublishAot=true -o .local/dataasset-spike/native
.local/dataasset-spike/native/DataAssetSpike --closed
dotnet run -c Release --project ../Bricks/org.mass4.turian.ddd/Brick~/Benchmarks/Turian.DataAssetDriven.Benchmarks -p:NoLocalPackages=true
```

MessagePipe 1.8.1 is restricted to the spike tool; gameplay never references its types. The normal open-generic registration fails in NativeAOT for GUID/struct types. Explicit closed registrations execute, but the subscription pipeline unconditionally inspects handler attributes through reflection and publishing emits trim warnings. The production decision is the internal fallback broker behind `IDataAssetEventBus`. A native broker check does not establish that the engine's complete exported launcher and asset serialization work under NativeAOT.

The new Studio brick copies precast only from Release builds. Existing Vulkan/UI precast targets still copy whichever configuration was built last; the repository-wide Release-only rule remains a review item.
