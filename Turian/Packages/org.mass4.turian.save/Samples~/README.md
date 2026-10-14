# Sample: game saves

Open this directory as a Turian project. `Assets/Project.dataasset` registers the player settings, graphics settings and client build profile. Open `Assets/save.prefab` and press Play, then click the Game view for input. The example enables the save, UI and Vulkan bricks through `Bricks/manifest.json`.

Three colored markers show the live player (top), the last local checkpoint (middle) and the simulated cloud checkpoint (bottom). The GUI brick draws an overlay with controls, live position/gold, each checkpoint's position/gold, operation progress and color-coded results. Empty slots show `empty`; conflicts retain both checkpoint readouts. Storage operations leave gameplay running and apply loaded state on the simulation thread. Output reports the local save directory, which defaults to the operating system's temporary directory under `turian-example-11-save`. Local slots persist between Play sessions; the cloud provider is in memory and resets each session.

| Key | Action |
| --- | --- |
| A / D | Move the live player without using Studio's arrow-key focus navigation. |
| G | Add ten gold to live game state. |
| S | Capture and save the player's stable ID, position and gold. |
| L | Load the current local checkpoint. |
| B | Restore the previous local generation into live game state. |
| C | Push the local checkpoint to simulated cloud storage. |
| R | Simulate another device changing the cloud checkpoint. |
| P | Explicitly choose the cloud checkpoint and restore it locally. |

For backup recovery: move, press G and S, then move and save again. B restores the first generation; L restores the second. For cloud conflicts: press S, C, R, then C again. The push reports a conflict and preserves both generations. P chooses the remote generation. Operations are serialized by the controller's `Busy` flag, so wait for each result before requesting the next one.

`Assets/SaveShowcase.cs` contains the component's `ISaveable<CampaignState>` capture/restore implementation, main-thread dispatch, explicit local/cloud revision handling and the GUI brick's `UiDocumentComponent.OnBuild` overlay. It uses the stable type ID from `SaveShowcase.cs.meta`, as normal project scripts do; there is no explicit `TypeId` attribute. `CampaignState` is a plain DTO; it stores the player's stable authored ID instead of a live node reference. The remote backend is simulated; real cloud adapters must enforce conditional writes on their service.

## Headless checks

From the example directory, run `dotnet run --project SceneSmoke -p:TurianDir=/media/work/dev/Turian`. This compiles the same scene scripts used by Studio, registers the script's `.cs.meta` ID, loads `Assets/save.prefab`, exercises A/D movement through `SceneTicker`, verifies local/backup/cloud restores and their HUD readouts, and checks that loaded state is applied on the simulation thread. It prints `SAVE SCENE SMOKE PASS` and uses disposable storage.

Run `dotnet run --project Smoke -p:TurianDir=/media/work/dev/Turian` for the separate persistence API checks: schema upgrades, a Brotli serializer plugin, legacy encoding readers, AES-GCM encryption, cloud conflicts, stable references, a 10,000-entity deterministic replay and killing a child process during an actual write. This console program does not launch Studio or load the scene.

`Samples~` deliberately bundles this optional example with the save brick. It is separate from the runtime library compiled under `Source~`. `Smoke` and `SceneSmoke` are development verification programs. A copy of this complete project lives at `/media/work/gamedev/turian/turian-examples/example-11-save`.
