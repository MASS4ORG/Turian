# Game Saves

Enable `org.mass4.turian.save` to use `Turian.Engine.Save`. This brick has no engine, renderer, networking SDK or third-party serializer dependency. It works in clients, dedicated servers and headless tests. Vendor storage and serialization integrations should be separate bricks depending on this one; do not make them dependencies of Engine.Core.

## Capture, persist, restore

Capture an owned DTO on the simulation thread, then call `SaveService.SaveAsync`. Serialization and checksum work run on a worker; storage is asynchronous. Never pass live nodes, mutable game collections, assets or service instances. Copy collections when capturing. Snapshot capture itself is game code: large worlds may need incremental snapshots or copy-on-write to meet a frame budget. Worker scheduling alone does not guarantee hitch-free capture.

```csharp
var storage = FileSaveStorage.ForGame(new SaveSlot("my-game"), new SaveSlot("player-1"));
var saves = new SaveService(storage, "my-game:campaign", schemaVersion: 1);
var slot = new SaveSlot("campaign-1");
var previous = await saves.LoadAsync<CampaignState>(slot);
var revision = await saves.SaveAsync(slot, ownedSnapshot, metadata, previous?.Revision);
```

`LoadedSave<T>` contains detached state. Validate it, create all entities, resolve references by their persistent IDs, then apply state on the simulation thread. `ISaveable<T>` is an optional capture/restore contract for plain entities or components. The game assigns identity; array position, instance IDs, CLR type names and hierarchy paths are unsuitable persistent identifiers. Do not automatically reflect an entire scene graph or mutate live objects while loading. Unknown required mod data must fail compatibility validation before applying any state.

Existing `RuntimeSave` and `SimulationSession.CaptureSave/RestoreSave` remain the engine's simulation checkpoint and asset-reference contracts. Store the JSON returned by `RuntimeSave.Write` as a string DTO through this service, then call `RuntimeSave.ReadAsync` with the receiving session's asset loader before `RestoreSave`. This retains required-extension checks, content fingerprints and the receiving loader's asset ownership.

## Schema and encoding

The storage envelope has its own version; the game supplies a stable schema ID and a positive schema version. Register `ISaveMigration` steps for each old version and serializer ID. Each step upgrades exactly one version and receives detached encoded bytes. Loading does not rewrite stored saves. Register old serializers as readers when switching the writer to another encoding. A MemoryPack, MessagePack or protobuf brick implements `ISaveSerializer`; no such package is required by the default JSON implementation. `JsonSaveSerializer` accepts options with generated type metadata for game DTOs.

Registrations and serializer configuration are fixed at service construction. Serializers, migration objects and compatibility callbacks must be safe for concurrent worker use. A migration for encrypted encodings must decrypt, upgrade the inner schema and re-encrypt; alternatively load the legacy schema with its old service and explicitly write through a new service. Migrations do not change schema identity or encoding implicitly.

The envelope stores metadata and payload together in a checksummed body. Byte arrays use JSON base64 so checksums cover the exact original bytes regardless of JSON formatting. SHA-256 detects corruption, not cheating. `AesGcmSaveSerializer` optionally authenticates and encrypts the payload; metadata remains visible and is not authenticated by AES. Provision keys outside save files, await outstanding operations before disposing the codec, and use different serializer IDs in custom adapters when implementing key rotation. Server-side authorization and validation remain necessary for multiplayer games.

## Storage, conflicts and recovery

`ISaveStorage` instances represent one game/account or server-world scope. Null expected revision means **create only**. Updates and deletes require an observed opaque revision. A conflict leaves the destination unchanged. Both built-in providers assign new revision tokens on every commit, including identical data. Cloud providers must implement atomic compare-and-write using their ETag, transaction or write-lock facilities; a separate read followed by an unconditional upload violates the contract.

`FileSaveStorage` writes a unique temporary file in the destination directory, flushes it to disk, then moves it into an empty slot or replaces an existing slot with a backup. A persistent per-slot lock file coordinates cooperating .NET processes. A busy writer reports a conflict; read the current revision and retry through game policy. Unfinished `.tmp` files are ignored. Slot names are canonical lowercase; prefixed paths avoid reserved Windows device names. The default local root uses `LocalApplicationData`; sandboxed consoles and mobile platforms should supply their platform-approved directory or adapter. Use separate directories per account/world, and keep the directory private to the application. Filesystem aliases, external editors and network filesystems are outside the coordination contract.

Process interruption before replacement leaves the committed slot intact. A completed replacement preserves the previous generation in `.bak`. Power-loss durability depends on OS/filesystem rename and directory-flush guarantees; this portable implementation does not fsync directory entries. Do not promise power-loss safety on all filesystems.

Primary corruption is reported. `LoadBackupAsync` explicitly reads the previous generation so the game can offer recovery rather than silently losing progress. A backup revision identifies the backup, not the primary: restore into a new slot or observe the current primary revision before replacing it. Corrupt file headers require repair/quarantine outside the ordinary slot API. Readers and writers enforce bounded sizes, and all paths derive from portable slot identifiers.

## Cloud and multiplayer

`SaveTransfer.CopyAsync` pushes or pulls exact bytes between any two adapters, requiring the observed source revision and observed destination revision. It transfers the source generation read during that operation; a later source edit remains a separate local change. Keep the last synced remote revision in a game-owned sync journal. On conflict, retain both snapshots, let the player choose or invoke a game-specific merge, and retry with the newly observed destination revision. Timestamps are display metadata, never conflict authority. The included cloud sample uses an in-memory remote to demonstrate these semantics without credentials.

Offline retry queues, provider authentication, account isolation, tombstones for synchronized deletion, cross-device conflict UI and game-specific merges belong to the cloud integration/game policy. Transfers are per-slot, not distributed transactions. A cloud SDK that cannot conditionally commit should not expose this storage contract without an authoritative gateway.

For multiplayer, capture at an authoritative tick with RNG state and content/mod versions. Store player IDs and entity IDs in DTOs. The server decides which client can request a save or restore; client-provided data must never directly restore a server world. The API does not implement network replication or distributed world ownership.

## Samples and scope

`Samples~` bundles an optional complete Studio project with an `Assets` folder, startup scene, registered settings and build profile. Its source is separate from the runtime library under `Source~`. `Assets/SaveShowcase.cs` demonstrates live capture/restore, local backups and simulated cloud conflicts, with a GUI brick overlay and A/D movement. Project script types use their `.cs.meta` identities. `SceneSmoke` loads that actual scene to verify controls, HUD readouts and simulation-thread restoration. `Smoke` is a separate console program for schema upgrades, serializer plugins, encryption, process interruption and deterministic replay. The complete project is also provided as `example-11-save` in the examples repository.

This brick implements the runtime persistence API. Steam/console/cloud SDK adapters, a full offline synchronization coordinator, Studio slot inspection/editing and play-mode reset controls, and the shared typed preferences API are separate integrations still pending from #89. Preferences should use the existing settings profiles rather than treating a game save as a general settings database.
