# ADR 0004: In-Game GUI — UIDoc / Guinevere

**Status**: Implemented (C1–C5)

## Context
Turian needs an in-game GUI system for runtime UI (HUD, menus, dialogs). Codename "Guinevere". Decision scope: framework, node model, coupling to engine, and input routing.

## Decision
- **Framework**: DVUI as `gui` namespace. User is dvui co-maintainer.
- **Module split**: `engine/ui/` (dvui-free data + logic), `subsystems/ui_render/` (shared tree-walk), `studio/` (editor panels).
- **Waist points**: only 4 couplings between Guinevere and engine: (1) `ui_document` component in Component.zig, (2) `.uidoc` asset type, (3) `UiEvents` service behind Services/Frame, (4) `ui_render.drawTree`/`dispatchClicks`.
- **Pay-for-use**: GameCodegen emits gui/ui_render deps only if project references `.uidoc`. Zero dvui linkage without it.
- **UiNode model**: `UiNode{guid, parent, item, style, components[]}` with `UiComponent` union. Layout = container row|column + `LayoutItem`. No RectTransform — `rect` is explicit opt-out.
- **Events**: strings at rest → load-time `EventId` → type-based struct API. `EventBinding` union (v1 named, reserved target{guid,endpoint}).
- **Input priority**: SDL events → dvui first → if `.handled`, suppress game input for that event type. Motion + gamepad never suppressed.

## Consequences
- Stable dvui IDs from node GUIDs via `id_extra` (hard requirement — dvui keys animation/focus on widget ID).
- `leadingGapMargin` shrinks explicit-rect nodes — zero `extra_margin` when `item.rect != null`.
- Play mode has zero live UI simulation (deferred).
