# ADR 0008: Input System

**Status**: Implemented

## Context
Engine needs device-agnostic input: keyboard, mouse, gamepad support, runtime rebinding, and data-driven binding assets. Must coexist with GUI (dvui) input consumption and support both polling and event-driven use.

## Decision
- **Per-frame snapshot model** (`engine/Input.zig`): keyboard (`EnumSet(Key)` cur+prev), mouse (buttons/pos/delta/wheel), action map (MAX_ACTIONS=64). Polling: raw (`isKeyDown`/`wasKeyPressed`/`mouseDelta`) + semantic (`isPressed`/`wasPressed`/`axis`/`vector`). Host lifecycle: `newFrame()` → setKey/setMouse/... → consume.
- **Analog model**: `value() f32` = max over sources (button=0/1, stick/trigger=deadzoned magnitude). PRESS_THRESHOLD=0.5.
- **Input Actions asset** (`.inputactions` ZON): actions[] with name, kind (button/axis/vector), sources[device+code]. `loadFromBytes` → `applyTo(*Input)`. Generated game enumerates all inputactions assets.
- **Gamepad**: SDL3 enum order for `GamepadButton` + `GamepadAxis`, locked by test. DEADZONE=0.15 with `applyDeadzone` rescaling. Generated game opens SDL_INIT_GAMEPAD.
- **Runtime rebinding**: `captureBinding()` waits for first newly-pressed input; `rebind(action, role, index, binding)` replaces or appends.
- **Input priority**: SDL events → dvui `addEvent` → `gui.events()` scanned for `.handled` → remaining events applied to `g_input`, withholding button/wheel/key the GUI claimed. Motion + gamepad never suppressed.

## Consequences
- Input pipeline is deterministic per-frame — scripts can't observe mid-frame state changes.
- `.inputactions` assets are the canonical binding definition; `configureInput` retained as code-first escape hatch.
- Rebinding persistence requires #13 (ProjectSettings) — deferred.
- `hydrateComponent` doesn't hydrate AssetRef fields on input action components — known gap.
