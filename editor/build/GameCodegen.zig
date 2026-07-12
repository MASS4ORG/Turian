/// Zig source-file generators — build.zig and main.zig for a standalone game.
/// Pure functions; no I/O, no GUI dependency.
///
/// This module re-exports the split codegen submodules for backward compatibility.
/// For new code, prefer importing from the focused modules directly:
/// - CodegenShared: types and utilities
/// - BuildZigCodegen: generateBuildZig
/// - ReflectionBuildZigCodegen: generateReflectionBuildZig
/// - MainZigCodegen: generateMainZig
const std = @import("std");

pub const Shared = @import("codegen/CodegenShared.zig");
pub const BuildZig = @import("codegen/BuildZigCodegen.zig");
pub const ReflectionBuildZig = @import("codegen/ReflectionBuildZigCodegen.zig");
pub const MainZig = @import("codegen/MainZigCodegen.zig");

// Re-export types for backward compatibility
pub const RuntimeConfig = Shared.RuntimeConfig;
pub const BuildConfig = Shared.BuildConfig;
pub const ModuleSpec = Shared.ModuleSpec;
pub const PluginSpec = Shared.PluginSpec;
pub const NativeLibSpec = Shared.NativeLibSpec;

// Re-export utility functions for backward compatibility
pub const normPath = Shared.normPath;
pub const absUnder = Shared.absUnder;
pub const sdl3LibPath = Shared.sdl3LibPath;
pub const appendKtx2Module = Shared.appendKtx2Module;

// Re-export generator functions for backward compatibility
pub const generateBuildZig = BuildZig.generateBuildZig;
pub const generateReflectionBuildZig = ReflectionBuildZig.generateReflectionBuildZig;
pub const generateMainZig = MainZig.generateMainZig;
