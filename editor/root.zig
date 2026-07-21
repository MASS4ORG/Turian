/// Engine module re-exported for convenience.
pub const engine = @import("engine");
/// Editor display name.
pub const name = "Turian Editor";
/// Editor version (matches engine version).
pub const version = engine.version;

/// User script scanner (discovers `is_component` types in assets via the Zig AST).
pub const scanner = @import("assets/Scanner.zig");
/// UI event type scanner.
pub const event_scanner = @import("assets/EventScanner.zig");
/// Project open/create operations.
pub const project_ops = @import("project/ProjectOps.zig");
/// Scene serialization (save/load .json files).
pub const scene_io = @import("project/SceneIo.zig");
/// Prefab system: instantiate scene assets as linked, overridable subtrees
/// with source-edit propagation. A prefab is just a scene asset.
pub const prefab = @import("project/Prefab.zig");
/// Asset .meta file management.
pub const asset_meta = @import("assets/AssetMeta.zig");
/// Asset cache path management and maintenance.
pub const asset_cache = @import("assets/AssetCache.zig");
/// Asset import pipeline (source → cached artifact).
pub const asset_importer = @import("assets/AssetImporter.zig");

pub const model_materials = @import("assets/ModelMaterials.zig");
/// Asset packaging (cooked artifacts → .oap package).
pub const asset_packager = @import("assets/AssetPackager.zig");
/// Asset type registry and lookup.
pub const asset_registry = @import("assets/AssetRegistry.zig");
/// Game build system (generates + compiles standalone game).
pub const GameBuild = @import("build/GameBuild.zig");
/// Play-mode build system (generates + compiles the in-editor play library).
pub const PlayBuild = @import("build/PlayBuild.zig");
/// SDK layout detection and BuildConfig resolution.
pub const sdk_layout = @import("build/SdkLayout.zig");
/// User script reflection via dynamic library compilation.
pub const user_reflection = @import("UserReflection.zig");

pub const ComponentDef = scanner.ComponentDef;
pub const DefKind = scanner.DefKind;
pub const SceneScriptField = @import("types/SceneScriptField.zig").SceneScriptField;
/// Data-asset instance I/O (load/save/merge).
pub const data_asset_io = @import("assets/DataAssetIo.zig");
pub const DataAssetFile = data_asset_io.DataAssetFile;
pub const AssetType = @import("types/AssetType.zig").AssetType;
pub const AssetDescriptor = @import("types/AssetDescriptor.zig").AssetDescriptor;
pub const OpenMode = @import("types/AssetDescriptor.zig").OpenMode;
pub const Guid = @import("guid").Guid;
pub const MetaFile = @import("types/MetaFile.zig").MetaFile;
pub const SubAsset = @import("types/MetaFile.zig").SubAsset;
const import_settings_types = @import("types/ImportSettings.zig");
pub const ImportSettings = import_settings_types.ImportSettings;
pub const ImageImportSettings = import_settings_types.ImageImportSettings;
pub const ModelImportSettings = import_settings_types.ModelImportSettings;
pub const FontImportSettings = import_settings_types.FontImportSettings;
pub const TextureType = import_settings_types.TextureType;
pub const ColorSpace = import_settings_types.ColorSpace;
pub const TextureCompression = import_settings_types.TextureCompression;
pub const ImageFilter = import_settings_types.ImageFilter;
pub const ImageWrap = import_settings_types.ImageWrap;

/// Centralised asset index — single source of truth for all project assets.
pub const AssetDatabase = @import("assets/AssetDatabase.zig").AssetDatabase;
pub const AssetInfo = @import("assets/AssetDatabase.zig").AssetInfo;
pub const ChangeKind = @import("assets/AssetDatabase.zig").ChangeKind;
/// Progress + cancellation interface for long-running operations.
pub const Progress = @import("Progress.zig").Progress;
/// Thread-safe registry of running/queued/finished editor tasks.
pub const TaskManager = @import("TaskManager.zig");
pub const Task = TaskManager.Task;
pub const TaskStatus = TaskManager.Status;
pub const TaskKind = TaskManager.Kind;

/// Cascading JSON settings store (global + project layers).
pub const settings = @import("project/Settings.zig");
pub const Settings = settings.Settings;
/// Typed schema for Studio-wide configuration, drawn by
/// `studio/SettingsEditor.zig` via the shared `PropDraw` reflection system.
const studio_settings_types = @import("project/StudioSettings.zig");
pub const StudioSettings = studio_settings_types.StudioSettings;
pub const StudioSettingsCategoryMeta = studio_settings_types.CategoryMeta;
pub const studio_settings_categories = studio_settings_types.categories;
/// MRU recent-projects list persisted via the Settings API.
pub const recent_projects = @import("project/RecentProjects.zig");
/// Studio-wide `.uitheme` discovery (built-ins + global themes folder).
pub const ThemeManager = @import("project/ThemeManager.zig");
/// Package manifest parser for `turian-package.json`.
pub const PackageManifest = @import("package/PackageManifest.zig").PackageManifest;
pub const PackageType = @import("package/PackageManifest.zig").PackageType;
/// Package discovery and dependency graph .
pub const PackageManager = @import("package/PackageManager.zig").PackageManager;
pub const checkEngineCompat = @import("package/PackageManager.zig").checkEngineCompat;
/// Project configuration (`project.json`) — source of truth for project
/// identity and dependencies; generates `build.zig.zon` .
pub const project_config = @import("project/ProjectConfig.zig");
pub const ProjectConfig = project_config.ProjectConfig;
/// Central, machine-wide package store shared across projects .
pub const package_store = @import("package/PackageStore.zig");
/// Runtime path-based menu tree builder for cascaded menus.
pub const menu_tree = @import("MenuTree.zig");

/// `path:line` reference parser for the Studio Output panel.
pub const log_location = @import("LogLocation.zig");

// Imported at root scope (not just re-exported from within `shortcuts`
// below) so `refAllDecls` in this file's own `test` block reaches their
// `test` blocks — see the identical concern noted on that block.
pub const shortcuts_binding = @import("shortcuts/Binding.zig");
pub const shortcuts_registry = @import("shortcuts/Registry.zig");

/// Studio shortcut binding model + command registry (parse/format/conflict
/// detection, persisted through `Settings`). GUI-free; `studio/services/Shortcuts.zig`
/// bridges it to dvui key events.
pub const shortcuts = struct {
    pub const Binding = shortcuts_binding.Binding;
    pub const Stroke = shortcuts_binding.Stroke;
    pub const Key = shortcuts_binding.Key;
    pub const Registry = shortcuts_registry.Registry;
    pub const Context = shortcuts_registry.Context;
    pub const CommandDesc = shortcuts_registry.CommandDesc;
    pub const Override = shortcuts_registry.Override;
    pub const BoundOverride = shortcuts_registry.BoundOverride;
    pub const MAX_OVERRIDE_BINDINGS = shortcuts_registry.MAX_OVERRIDE_BINDINGS;
    pub const Entry = shortcuts_registry.Entry;
    pub const Conflict = shortcuts_registry.Conflict;
};

// Top-level (not nested inside `i18n` below) so this file's own `test`
// block reaches their `test` blocks — see the note above `shortcuts_binding`.
pub const i18n_extractor = @import("i18n/Extractor.zig");
pub const i18n_compiler = @import("i18n/Compiler.zig");

/// Localization editor tooling (ADR 0011): `tr`/`trc`/`trn`/`trKey` call-site
/// extraction and `.strings` -> `.strtab` compilation.
pub const i18n = struct {
    pub const Extractor = i18n_extractor;
    pub const Compiler = i18n_compiler;
};

test {
    // Force every re-exported module to be analysed so their `test` blocks
    // are collected by the test runner. Without this, `addTest` on the
    // editor module discovers zero tests (the root file has no direct
    // tests; everything is reached only through `pub const X = @import(...)`)
    // — see the identical block in engine/root.zig.
    @import("std").testing.refAllDecls(@This());
}
