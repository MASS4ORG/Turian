/// Engine module re-exported for convenience.
pub const engine = @import("engine");
/// Editor display name.
pub const name = "Turian Editor";
/// Editor version (matches engine version).
pub const version = engine.version;

/// User script scanner (discovers `is_component` types in assets via the Zig AST).
pub const scanner = @import("assets/Scanner.zig");
/// UI event type scanner (#112: discovers `event_name` declarations in assets
/// via the Zig AST, feeding the Studio inspector's event dropdown).
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
/// Package manifest parser for `turian-package.json` (issue #56/#58).
pub const PackageManifest = @import("package/PackageManifest.zig").PackageManifest;
pub const PackageType = @import("package/PackageManifest.zig").PackageType;
/// Package discovery and dependency graph (issue #58).
pub const PackageManager = @import("package/PackageManager.zig").PackageManager;
pub const checkEngineCompat = @import("package/PackageManager.zig").checkEngineCompat;
/// Project configuration (`project.json`) — source of truth for project
/// identity and dependencies; generates `build.zig.zon` (issue #57).
pub const project_config = @import("project/ProjectConfig.zig");
pub const ProjectConfig = project_config.ProjectConfig;
/// Central, machine-wide package store shared across projects (issue #20).
pub const package_store = @import("package/PackageStore.zig");

test {
    // Force every re-exported module to be analysed so their `test` blocks
    // are collected by the test runner. Without this, `addTest` on the
    // editor module discovers zero tests (the root file has no direct
    // tests; everything is reached only through `pub const X = @import(...)`)
    // — see the identical block in engine/root.zig.
    @import("std").testing.refAllDecls(@This());
}
