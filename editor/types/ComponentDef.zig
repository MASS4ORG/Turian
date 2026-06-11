const std = @import("std");
const engine = @import("engine");
const FieldDef = @import("FieldDef.zig").FieldDef;

/// Maximum length of a component type name.
pub const MAX_COMP_NAME = 128;
/// Maximum length of a component source file path.
pub const MAX_COMP_FILE = 256;
/// Maximum number of discovered components (builtin + user).
pub const MAX_COMPONENTS = 64;
/// Maximum number of reflected fields per component.
pub const MAX_COMP_FIELDS = 16;

/// Describes a component (builtin or user script) for the editor.
pub const ComponentDef = struct {
    type_name: [MAX_COMP_NAME]u8 = std.mem.zeroes([MAX_COMP_NAME]u8),
    type_name_len: usize = 0,
    source_file: [MAX_COMP_FILE]u8 = std.mem.zeroes([MAX_COMP_FILE]u8),
    source_file_len: usize = 0,
    is_builtin: bool = false,
    fields: [MAX_COMP_FIELDS]FieldDef = std.mem.zeroes([MAX_COMP_FIELDS]FieldDef),
    field_count: usize = 0,

    pub fn typeName(self: *const ComponentDef) []const u8 {
        return self.type_name[0..self.type_name_len];
    }

    pub fn sourceFile(self: *const ComponentDef) []const u8 {
        return self.source_file[0..self.source_file_len];
    }

    pub fn setTypeName(self: *ComponentDef, n: []const u8) void {
        const len = @min(n.len, MAX_COMP_NAME);
        @memcpy(self.type_name[0..len], n[0..len]);
        self.type_name_len = len;
    }

    pub fn setSourceFile(self: *ComponentDef, p: []const u8) void {
        const len = @min(p.len, MAX_COMP_FILE);
        @memcpy(self.source_file[0..len], p[0..len]);
        self.source_file_len = len;
    }

    /// Returns the human-readable display name (uses BUILTIN_COMPONENTS for builtins).
    pub fn displayName(self: *const ComponentDef) []const u8 {
        if (self.is_builtin) {
            for (engine.BUILTIN_COMPONENTS) |b| {
                if (std.mem.eql(u8, b.type_name, self.typeName())) return b.display_name;
            }
        }
        return self.typeName();
    }
};
