# ADR 0011: Localization (i18n)

**Status**: In progress (core runtime implemented; asset pipeline, editor
tooling, Studio/game adoption pending — see `docs/plans/localization.md`)

## Context
Studio (#123) and shipped games (#36) both need translated text. Without a
shared system, Studio would grow its own ad hoc translation layer independent
from what games need, duplicating work and diverging over time.

## Decision
- **One system, two entry points**, both living in `engine/` so Studio and the
  shipped game share the same core. `tr("Open Scene…")` is source-keyed, for UI
  chrome (compiles to a build error on a non-literal argument — extraction
  completeness is enforced by the compiler, not a linter). `Locale.key("dlg.act1.intro")`
  is id-keyed, for game content authored by designers in a `.strings` asset.
  Missing translations degrade differently: `tr()` falls back to the English
  source; `Locale.key()` falls back to `⟦the.key⟧` in debug and the bare key in
  release.
- **ICU MessageFormat, subset, not Fluent.** `{name}`, `{count, plural, one {#
  file} other {# files}}`, `{gender, select, male {He} other {They}}`. Fluent
  was rejected: a second parser and mental model for no capability gain, and
  the vendor tooling this is meant to interop with (Crowdin, POEditor, Lokalise)
  speaks ICU natively. Plurals use CLDR cardinal rules from a generated table,
  not hand-written per-language `if` chains.
- **`.strings` JSON asset is the source of truth; XLIFF/CSV are bridges.** The
  schema is a 1:1 model of an XLIFF `<trans-unit>` (id, source, target, note,
  state), so both directions of the XLIFF/CSV round-trip are total by
  construction. XLIFF was rejected as the on-disk format (#123's original ask)
  because it would be a foreign body in the AssetDatabase/MetaFile/`.oap`
  pipeline; `serde.json` is already the house serializer for assets, and this
  keeps the XML dependency confined to editor tooling so it never links into
  the shipped game.
- **Runtime table is compiled, read-only, and hot-swappable.** `.strings` bakes
  to a compact binary `.strtab` (header, sorted id array, offset array, blob),
  packed into `game.oap`. Lookup is binary search comparing full id strings —
  a hash may pre-filter but never decide, since a silent hash collision in a
  shipped game is unfixable from the field. Locale switch atomically swaps the
  active table pointer and bumps a `generation` counter; the previous blob is
  never freed, so slices already handed out stay valid. Because both Studio
  and in-game UI (`.uidoc`) are immediate-mode, switching locale without a
  scene reload falls out for free — text is re-fetched every frame. No
  refresh event/signal system exists or is needed; `generation` exists only
  for consumers that cache text (e.g. baked text meshes).
- **Per-locale fonts, not glyph-fallback chains.** dvui's `Font` carries a
  single family name with no multi-font fallback. Locale font override swaps
  the family name at locale-switch time; mixed-script strings are handled by
  shipping a font with the needed coverage (Noto Sans / Noto Sans CJK), not by
  chaining. If this proves insufficient a fallback chain would be a dvui MR,
  not in scope here.
- **Out of scope**: RTL layout mirroring, BiDi shaping, machine translation,
  and runtime hot-reload of translation files. A `direction: enum { ltr, rtl }`
  field is reserved on locale metadata so the door stays open.

## Consequences
- `engine/i18n/` ships in every game; XLIFF/CSV I/O and the extractor live in
  `editor/i18n/` only, so the XML dependency never reaches the shipped binary.
- A non-literal argument to `tr`/`trc`/`trn` is a compile error, not a review
  promise — this is why they take `comptime` message parameters.
- Studio's ~355 hardcoded string sites need migration in tranches (one panel
  per commit), tracked separately from this ADR.
- See `docs/plans/localization.md` for the phased implementation plan and file
  layout.
