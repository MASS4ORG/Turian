//! Cross-platform dynamic-library loader for `dlopen`-ing the hot-compiled
//! play library. Delegates to `std.DynLib` on POSIX and provides a
//! `LoadLibraryW`/`GetProcAddress`/`FreeLibrary` implementation on Windows,
//! exposing a uniform `open`/`lookup`/`close` API.

const std = @import("std");
const builtin = @import("builtin");

const is_windows = builtin.os.tag == .windows;

const DynLib = @This();

inner: Inner,

const Inner = if (is_windows) WindowsDynLib else std.DynLib;

pub const Error = if (is_windows) error{ FileNotFound, OutOfMemory } else std.DynLib.Error;

/// Load the shared library at `path`. Trusts the file.
pub fn open(path: []const u8) Error!DynLib {
    return .{ .inner = try Inner.open(path) };
}

/// Resolve `name` to a symbol of type `T` (a function/data pointer), or null.
pub fn lookup(self: *DynLib, comptime T: type, name: [:0]const u8) ?T {
    return self.inner.lookup(T, name);
}

/// Unload the library.
pub fn close(self: *DynLib) void {
    self.inner.close();
}

// ── Windows backend ─────────────────────────────────────────────────────────
// `std.os.windows` ships no LoadLibrary/GetProcAddress bindings, so declare the
// three kernel32 entry points we need directly.
const WindowsDynLib = struct {
    const windows = std.os.windows;

    extern "kernel32" fn LoadLibraryW(lpLibFileName: windows.LPCWSTR) callconv(.winapi) ?windows.HMODULE;
    extern "kernel32" fn GetProcAddress(hModule: windows.HMODULE, lpProcName: [*:0]const u8) callconv(.winapi) ?windows.FARPROC;
    extern "kernel32" fn FreeLibrary(hModule: windows.HMODULE) callconv(.winapi) windows.BOOL;

    module: windows.HMODULE,

    fn open(path: []const u8) Error!WindowsDynLib {
        // LoadLibraryW wants a UTF-16, NUL-terminated path.
        var buf: [windows.PATH_MAX_WIDE:0]u16 = undefined;
        const len = std.unicode.utf8ToUtf16Le(&buf, path) catch return error.FileNotFound;
        buf[len] = 0;
        const module = LoadLibraryW(buf[0..len :0].ptr) orelse return error.FileNotFound;
        return .{ .module = module };
    }

    fn lookup(self: *WindowsDynLib, comptime T: type, name: [:0]const u8) ?T {
        if (GetProcAddress(self.module, name.ptr)) |proc| {
            return @ptrCast(@alignCast(proc));
        }
        return null;
    }

    fn close(self: *WindowsDynLib) void {
        _ = FreeLibrary(self.module);
        self.* = undefined;
    }
};
