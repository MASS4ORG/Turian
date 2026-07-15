# Localization (i18n) — Implementation Plan

Covers GitLab issues **#36** (in-game localization) and **#123** (Studio editor
translation). This document is the handoff brief for the implementing agent: it
fixes the architectural decisions, names the files to create, and orders the
work into independently shippable phases.

**Read first:** `docs/ADR/ADR-0005-di-frame-services.md` (service injection),
`docs/ADR/ADR-0009-scene-asset-pipeline.md` (asset pipeline),
`docs/ADR/ADR-0004-in-game-gui-uidoc.md` (in-game UI).

---

## 1. Decisions

These are settled. Deviating from them needs a written justification in the MR.

### D1 — One system, two entry points

Studio (#123) and the shipped game (#36) share a single localization core living
in `engine/`. There is no second implementation. Studio already depends on
`engine`, so this costs nothing.

The core exposes two ways to reach a string, because UI chrome and game content
have genuinely different authoring needs:

| Entry point | For | Key | Authored by |
|---|---|---|---|
| `tr("Open Scene…")` | UI chrome (Studio menus, buttons, in-game HUD labels) | the source string itself | programmers, in code |
| `Locale.key("dlg.act1.intro")` | game content (dialogue, item names, quests) | an explicit stable id | designers, in a `.strings` asset |

Both compile into the *same* table, are formatted by the *same* engine, and are
served by the *same* `Locale` service. The only difference is where the id comes
from. This mirrors how Unreal separates text literals from string tables, and it
avoids the two failure modes of picking only one: source-keyed-only makes
narrative content unrefactorable, key-only makes 355 Studio call sites
unreadable and lets a missing key render as `menu.file.open` to the user.

**Missing-translation behaviour differs accordingly**, and this is the point:
`tr()` can *always* fall back to the source string, so a missing translation
degrades to English, never to a raw key. `Locale.key()` falls back to
`⟦the.key⟧` in debug builds and the key in release.

### D2 — ICU MessageFormat (subset), not Fluent

Message syntax is a documented subset of ICU MessageFormat:

```
Hello, {name}!
{count, plural, one {# file} other {# files}}
{gender, select, male {He} female {She} other {They}} joined
```

Rejected Fluent (`.ftl`) as the primary format: it needs a second parser and a
second mental model, and the vendor tooling the issues care about (Crowdin,
POEditor, Lokalise) speaks ICU natively. ICU + CLDR plurals covers everything
both issues ask for. Do not add Fluent "as well" — one syntax.

Plurals use **CLDR plural rules**. Do not hand-write per-language `if` chains:
generate a comptime rule table from CLDR data (there are only ~20 distinct
cardinal rule sets covering every language). See T2.3.

### D3 — `.strings` JSON asset is the source of truth; XLIFF is a bridge

The project stores one `.strings` asset per locale, JSON via `serde.json`
(house rule: JSON over ZON for serialized assets), GUID'd and tracked by the
AssetDatabase like every other asset.

XLIFF 1.2 and CSV are **import/export formats**, not the on-disk truth. This is
a deliberate departure from #123's "XLIFF as the primary editable format", for
three reasons: an `.xliff` file sitting in `assets/` would be a foreign body in
the AssetDatabase/MetaFile/`.oap` pipeline; `serde.json` is already the house
serializer; and it keeps the XML dependency confined to editor tooling so it
never links into the shipped game.

**Losslessness is guaranteed by construction, not by care:** the `.strings`
schema is a 1:1 model of XLIFF's `<trans-unit>` (id, source, target, note,
state), so the mapping in both directions is total. A team that wants to live in
Crowdin runs `turian i18n export` / `import` and can enforce the round-trip in
CI. Write the round-trip property test (T3.5) before the exporter feels done.

### D4 — Runtime table is compiled, read-only, and hot-swappable

`.strings` → a compact binary `.strtab` blob at bake time, packed into
`game.oap` by the existing `AssetPackager`. Layout: header, sorted id array,
offset array, string blob. Lookup is a binary search over ids; `get()` for an
argument-less message returns a `[]const u8` **pointing into the blob** — zero
allocation, no copy.

Locale switch = atomically swap the active table pointer + bump
`generation: u32`. Loaded blobs are cached for the session and never freed, so
slices handed out before a switch stay valid (they just show the old locale
until re-fetched). Do not free the previous blob on switch — that is a dangling
`[]const u8` waiting to happen.

**Because dvui is immediate-mode, "switch locale without a scene reload" is
free**: text is re-fetched from the table every frame, for both Studio and
`.uidoc` game UI. The `generation` counter exists only for consumers that
*cache* text (baked text meshes, world-space labels). Do not build an
event/signal refresh system; there is nothing to refresh.

### D5 — Extraction completeness is enforced by the compiler

`tr` takes its message as a `comptime` parameter:

```zig
pub fn tr(comptime msg: []const u8) []const u8
pub fn trc(comptime ctx: []const u8, comptime msg: []const u8) []const u8      // disambiguating context
pub fn trn(comptime one: []const u8, comptime other: []const u8, n: u64) ...   // plural
```

A non-literal argument therefore **fails to compile**. That converts "did the
extractor miss a string?" from a QA problem into a build error, and it is the
whole reason to accept `comptime` here. Dynamic strings must go through
`Locale.key()`, which is a runtime lookup and may legitimately miss.

The extractor is a `std.zig.Ast` walker. **Reuse the existing precedent** —
`editor/assets/Scanner.zig` and `editor/assets/EventScanner.zig` already parse
Zig sources with `std.zig.Ast` and pull string literals out via
`std.zig.string_literal.parseAlloc`. Same machinery, different node type (call
expressions rather than container decls).

### D6 — Per-locale fonts, with a known constraint

**Constraint found in dvui** (`dvui/src/Font.zig`): a `Font` carries a *single*
`family: [NAME_MAX_LEN:0]u8`, and unresolved families silently fall back to the
embedded "Vera" face. There is **no multi-font glyph-fallback chain**. So:

- Per-locale font override works by *swapping the family name* — the mechanism
  already exists (`subsystems/ui_render/theme.zig` `withFontFamily`, and the
  register-once idiom in `studio/inspector/editor/FontRegistry.zig`, since
  dvui's `addFont` only appends and never replaces).
- Mixed-script strings (a Japanese UI containing a Latin product name) are
  handled *by picking a font with the coverage*, not by chaining. Ship Noto Sans
  / Noto Sans CJK.
- If mixed-script gaps bite in practice, the fix is a dvui MR adding a fallback
  chain. The user is a dvui co-maintainer; that is an acceptable escalation, but
  it is **not** in scope for the first pass. See R1.

### D7 — Explicitly out of scope

RTL layout mirroring, BiDi shaping, machine translation, and runtime hot-reload
of translation files. Reserve a `direction: enum { ltr, rtl }` field in the
locale metadata so the door stays open, and write the exclusions into the ADR.

---

## 2. Module layout

Respect the module boundaries in `CLAUDE.md`: `engine/` = runtime (ships in the
game), `editor/` = logic with no GUI, `studio/` = GUI only, `subsystems/` =
opt-in.

```
engine/i18n/
  root.zig            re-exports; refAllDecls test block
  Locale.zig          the service: active locale, fallback chain, get/key/tr, generation
  StringTable.zig     compiled .strtab reader (binary search, zero-alloc get)
  message.zig         ICU-subset parser + formatter (compiled instruction stream)
  plurals.zig         CLDR cardinal rule table (generated — see T2.3)
  locale_id.zig       BCP-47 tag parse/normalize/fallback ("pt-BR" -> "pt" -> default)
engine/assets/
  Strings.zig         the `.strings` DataAsset (XLIFF-equivalent schema, serde.json)

editor/i18n/
  Extractor.zig       std.zig.Ast walk; finds tr/trc/trn calls; mirrors Scanner.zig
  XliffIo.zig         XLIFF 1.2 read/write
  CsvIo.zig           RFC 4180 read/write
  Compiler.zig        .strings -> .strtab
  Checker.zig         missing/fuzzy/obsolete report; powers `check` + build warnings
  Pseudo.zig          pseudolocale generator (see T3.6)
editor/cli/
  CliI18n.zig         `turian i18n extract|export|import|check|compile|pseudo`

studio/string-table/  the String Table editor panel (grid, filters, per-locale columns)
subsystems/ui_render/ per-locale font family application (existing dvui seam)
```

Keep every file under 300 lines where you can, 500 hard (house rule). `message.zig`
is the one at real risk — split parser and formatter if it grows.

---

## 3. Phases

Each phase is independently mergeable and independently verifiable. Do not start
Studio migration (P3) before the core (P0–P2) is green.

### P0 — ADR + core runtime (no UI, no I/O)

- **T0.1** Write `docs/ADR/ADR-0011-localization.md` from §1 of this document.
  ADR-0010 is the current highest.
- **T0.2** `engine/i18n/locale_id.zig` — BCP-47 parse/normalize, fallback chain
  `pt-BR → pt → default`. Pure, trivially testable.
- **T0.3** `engine/i18n/plurals.zig` — CLDR cardinal rules. Generate the table
  (a small script that reads CLDR `plurals.xml` and emits Zig) rather than
  hand-writing it; commit the generated file. Test against CLDR's own sample
  data for at least: en, pt, fr, ru, pl, ar, ja, zh, cs, lt.
- **T0.4** `engine/i18n/message.zig` — ICU-subset parse → compiled instruction
  stream; format into a caller-supplied buffer or arena. Support `{name}`,
  `{n, plural, ...}` with `#`, `{x, select, ...}`, and `'` escaping.
- **T0.5** `engine/i18n/StringTable.zig` — `.strtab` binary format + reader.
  Store the full id strings and compare on lookup (a 64-bit hash may pre-filter,
  but never *decide*; silent hash collisions in a shipped game are unfixable
  from the field). Golden-file test.
- **T0.6** `engine/i18n/Locale.zig` — the service. `tr`/`trc`/`trn`,
  `key`, `setLocale`, `generation`, fallback chain, debug `⟦key⟧` marker.
  Register in `engine.Services`; expose convenience accessors on `engine.Frame`
  (`frame.tr(...)` formats into the frame arena).

**Acceptance:** `zig build test` green; plural conformance tests pass; no GUI or
filesystem code in `engine/i18n/`.

### P1 — `.strings` asset + pipeline wiring

- **T1.1** `engine/assets/Strings.zig` — the asset. Schema mirrors XLIFF
  `<trans-unit>`:
  ```jsonc
  {
    "version": 1,
    "locale": "ja",
    "units": [
      { "id": "…", "source": "Open Scene…", "target": "シーンを開く…",
        "note": "File menu item", "state": "translated" }  // new|needs-review|translated|final
    ]
  }
  ```
- **T1.2** Register the type: `AssetType.strings`, extension `.strings`,
  `AssetDescriptor` (name, `internal_editor`, an icon hint), `lookupByFilename`.
  Files: `editor/types/AssetType.zig`, `editor/assets/AssetRegistry.zig`.
- **T1.3** AssetDatabase + MetaFile + GUID wiring, following `.uitheme` as the
  worked example (it is the most recent asset added and the cleanest template).
- **T1.4** Packaging: `.strings` → `.strtab` on bake, into `game.oap` via
  `editor/assets/AssetPackager.zig`. The shipped game must never see JSON or XML.
- **T1.5** `ProjectSettings.localization`: `default_locale`, `available_locales`
  (each with `code`, `font` GUID, `fallback`, `direction`). Bump
  `CURRENT_VERSION` and add the migration — absent section keeps old projects
  loading (existing `migrate` pattern).

**Acceptance:** a `.strings` asset imports, appears in the Asset Browser with a
GUID and `.meta`, and lands in `game.oap` as a `.strtab`.

### P2 — Editor tooling + CLI

- **T2.1** `editor/i18n/Extractor.zig` — `std.zig.Ast` walk over configured
  roots (engine/editor/studio for Studio's own catalog; project scripts for the
  game's). Emits id, source, plural forms, context, note, and source location.
  **Id = FNV-1a 64 of `context \x04 source`, rendered hex** — deterministic,
  stable across machines, and independent of file position.
- **T2.2** `editor/i18n/XliffIo.zig`. Needs XML. **Do not hand-roll a parser**
  — `ianprime0509/zig-xml` (v0.2.0) is already present in a dependency cache
  under `examples/3d-model-materials/.cache/`; add it as a proper `build.zig.zon`
  dependency of the **editor module only**. First task: confirm it builds on Zig
  0.16 (see R2). Map: `<trans-unit id>` ← id, `<source>`, `<target>`,
  `<note>` ← note, `state` ← state, `<group>` for plural forms.
- **T2.3** `editor/i18n/CsvIo.zig` — RFC 4180. Handle quoting, embedded commas
  and newlines, and emit a UTF-8 BOM so Excel does not mangle non-ASCII.
- **T2.4** `editor/i18n/Compiler.zig` — `.strings` → `.strtab`.
- **T2.5** `editor/i18n/Checker.zig` — missing / fuzzy / obsolete report.
- **T2.6** **Merge semantics** (the part that is easy to get wrong): re-running
  extract must *preserve* existing translations. Same id → keep target. Source
  text changed → keep the target but set `state: needs-review` (fuzzy). Id gone
  from source → move to an `obsolete` array rather than deleting, so a renamed
  string can be recovered by a translator instead of retranslated.
- **T2.7** `editor/cli/CliI18n.zig` + `build.zig` steps `i18n-extract` and
  `i18n-check`; wire `i18n-check` into the `ci` step as a warning (`--strict`
  promotes to an error).

**Acceptance:** `turian i18n extract` produces a valid XLIFF; a translated XLIFF
imports; the CSV and XLIFF round-trips are lossless (property test); `check`
reports missing keys.

### P3 — Studio adoption (#123)

- **T3.1** Migrate Studio's ~355 user-facing string sites to `tr()`. **Do this in
  tranches, one panel per commit** (Inspector, Asset Browser, Scene Hierarchy,
  menus, Settings, dialogs) — a single 74-file diff is unreviewable and will bury
  a real bug.
- **T3.2** Language dropdown in the Settings panel. Persist to the editor
  `Settings` store (`editor/project/Settings.zig`) under `editor.language`; its
  existing `SubscriberFn` mechanism is exactly the hook for triggering the table
  and font swap — use it rather than adding a new observer.
- **T3.3** Ship Studio's own catalogs. Studio is not a project, so its `.strtab`
  files are `@embedFile`d (or laid out by `SdkLayout.zig`). Seed with `pt-BR`,
  `ja`, `zh-Hans` — the site (`../turian-site/content/docs/`) is already
  translated into exactly those, so the terminology is settled.
- **T3.4** Per-locale font swap on language change (D6).
- **T3.5** Round-trip property tests (`.strings` ↔ XLIFF ↔ CSV).
- **T3.6** `editor/i18n/Pseudo.zig` — a generated `qps-ploc` pseudolocale that
  accents and expands text ~40% (`Open Scene…` → `[Ôṗéñ Šçéñé… ⋯⋯]`). **Not in
  either issue, and worth more than any single translation**: it catches layout
  overflow and unextracted strings before a single translator is hired, and it
  makes the CLDR/format plumbing testable without a human in the loop.

**Verification (house rule — build+test alone does not count):** screenshot the
Studio via `studio/services/Screenshots.zig` in `en`, `ja`, and `qps-ploc`, and
FPS-check via the debug server on port 7778. Prefer `TURIAN_CAPTURE_AFTER_MS` /
`TURIAN_CAPTURE_QUIT` over manual debug-RPC round-trips (those are flaky). Never
use a desktop screenshot tool.

### P4 — Game runtime (#36)

- **T4.1** Boot: read `ProjectSettings.localization`, detect the OS locale on
  first run (SDL exposes preferred locales — verify the binding in `../sdl3`),
  persist the player's choice in game settings (#13).
- **T4.2** `.uidoc` text localization: add `localized: bool` / `key` to
  `TextComponent` in `engine/ui/UiDocument.zig`. Additive, version-bumped,
  migrated — a `.uidoc` authored today must keep loading.
- **T4.3** Per-locale font in the shipped game (`subsystems/ui_render/`).
- **T4.4** Example project demonstrating a runtime language switch with no scene
  reload — this is an acceptance criterion of #36 and should be demoable.

### P5 — Studio String Table panel + authoring polish

- **T5.1** `studio/string-table/` panel: key | source | translation | state grid,
  filter to missing/fuzzy, mark fuzzy, per-locale columns. Register `.strings` as
  `internal_editor` and give it a dock layout (the per-asset-type dock layout
  machinery landed in `94ac205`).
- **T5.2** Missing-translation warnings surfaced at build time (Checker → build
  step → TaskManager/TaskBar).
- **T5.3** "Mark for localization" annotation on Inspector string fields (#36).

### P6 — Docs

Update `../turian-site/content/docs/` (house rule): a manual page on the
localization workflow (author → extract → export → translate → import → check →
ship) and a contributor page on translating Studio itself.

---

## 4. Risks

| | Risk | Mitigation |
|---|---|---|
| **R1** | dvui has no multi-font glyph fallback (D6). A Japanese UI with Latin text needs one font covering both. | Ship Noto Sans CJK. If it bites, upstream a fallback-chain MR to dvui (we co-maintain). Do not block P3 on it. |
| **R2** | `zig-xml` 0.2.0 may not build on Zig 0.16. | Verify **before** committing to T2.2. Fallback: a ~200-line pull parser over the XLIFF subset we emit — acceptable because we control the writer, but it must still handle namespaces, CDATA, and XML entities from vendor output. |
| **R3** | The 355-site Studio migration is churn that can hide a real regression. | One panel per commit (T3.1); screenshot-verify each tranche. |
| **R4** | Hash-only ids collide silently and are unfixable in a shipped game. | Store and compare the full id string; hash is a pre-filter only (T0.5). |
| **R5** | `.uidoc` schema change breaks existing assets. | Additive field + version bump + migration (T4.2), following the `ProjectSettings`/`UiTheme` `migrate` pattern. |
| **R6** | Freeing the old table on locale switch dangles every `[]const u8` already handed out. | Cache blobs for the session; never free on switch (D4). |

---

## 5. Answering the issues directly

- **#123's `tr()` + XLIFF export + plurals + hints + Settings language picker** —
  P0, P2, P3. Every acceptance criterion is covered; the "no hardcoded strings"
  criterion is upgraded from a review promise to a compile error (D5).
- **#123's "evaluate Fluent"** — evaluated, rejected, reasons in D2.
- **#123's "compatible with #36 or a clear migration path"** — stronger than
  asked: it is literally the same code (D1).
- **#36's string tables, fallback chain, CLDR plurals, live switch, CSV/XLIFF
  round-trip, build warnings, per-locale fonts** — P1, P2, P4, P5. Live switch
  falls out of immediate-mode rendering (D4); no signal/event system needed.
- **#36's XLIFF-as-vendor-format** — kept, as a bridge rather than the source of
  truth (D3), with losslessness guaranteed by schema equivalence.
