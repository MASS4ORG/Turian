/// Engine module re-exported for convenience.
pub const engine = @import("engine");
/// Editor display name.
pub const name = "Turian Editor";
/// Editor version (matches engine version).
pub const version = engine.version;

/// User script scanner (discovers `is_component` types in assets via the Zig AST).
pub const scanner = @import("Scanner.zig");
/// Project open/create operations.
pub const project_ops = @import("ProjectOps.zig");
/// Scene serialization (save/load .json files).
pub const scene_io = @import("SceneIo.zig");
/// Prefab system: instantiate scene assets as linked, overridable subtrees
/// with source-edit propagation (issue #32). A prefab is just a scene asset.
pub const prefab = @import("Prefab.zig");
/// Asset .meta file management.
pub const asset_meta = @import("AssetMeta.zig");
/// Asset cache path management and maintenance.
pub const asset_cache = @import("AssetCache.zig");
/// Asset import pipeline (source → cached artifact).
pub const asset_importer = @import("AssetImporter.zig");
/// Asset packaging (cooked artifacts → .oap package).
pub const asset_packager = @import("AssetPackager.zig");
/// Asset type registry and lookup.
pub const asset_registry = @import("AssetRegistry.zig");
/// Game build system (generates + compiles standalone game).
pub const GameBuild = @import("GameBuild.zig");
/// Play-mode build system (generates + compiles the in-editor play library).
pub const PlayBuild = @import("PlayBuild.zig");
/// SDK layout detection and BuildConfig resolution.
pub const sdk_layout = @import("SdkLayout.zig");
/// User script reflection via dynamic library compilation.
pub const user_reflection = @import("UserReflection.zig");

pub const ComponentDef = scanner.ComponentDef;
pub const DefKind = scanner.DefKind;
pub const SceneScriptField = @import("types/SceneScriptField.zig").SceneScriptField;
/// Data-asset instance I/O (load/save/merge).
pub const data_asset_io = @import("DataAssetIo.zig");
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
pub const TextureType = import_settings_types.TextureType;
pub const ColorSpace = import_settings_types.ColorSpace;
pub const TextureCompression = import_settings_types.TextureCompression;
pub const ImageFilter = import_settings_types.ImageFilter;
pub const ImageWrap = import_settings_types.ImageWrap;

/// Centralised asset index — single source of truth for all project assets.
pub const AssetDatabase = @import("AssetDatabase.zig").AssetDatabase;
pub const AssetInfo = @import("AssetDatabase.zig").AssetInfo;
pub const ChangeKind = @import("AssetDatabase.zig").ChangeKind;
/// Progress + cancellation interface for long-running operations.
pub const Progress = @import("Progress.zig").Progress;
/// Thread-safe registry of running/queued/finished editor tasks.
pub const TaskManager = @import("TaskManager.zig");
pub const Task = TaskManager.Task;
pub const TaskStatus = TaskManager.Status;
pub const TaskKind = TaskManager.Kind;

/// Cascading JSON settings store (global + project layers).
pub const settings = @import("Settings.zig");
pub const Settings = settings.Settings;
/// MRU recent-projects list persisted via the Settings API.
pub const recent_projects = @import("RecentProjects.zig");
