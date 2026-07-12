//! Shared asset preview API (issues #19, #25): a single registry of
//! "asset type → thumbnail generator" used by both the Inspector's preview
//! panel and the Asset Browser's tile grid, with an in-memory + on-disk raster
//! cache keyed by the asset's GUID and its `.meta` `source_hash` (so a
//! thumbnail is regenerated only when its source actually changes — the same
//! change-detection the reimport pipeline already uses).
//!
//! Custom previewers: call `registerProvider(asset_type, yourFn)` (e.g. from
//! `studio/Main.zig` after `PreviewSystem.init()`) to replace or add a
//! generator for an asset type — the same fn-pointer-registry idiom
//! `GizmoSystem.registerGizmo` already uses for custom component gizmos. User
//! script `.so`s can't call this directly (they don't link the GUI/render
//! modules — see the #40 custom-editor plan), so today this is a Zig-source
//! extension point; a future editor-plugin loader (issue #4) would register
//! providers through the same call.
//!
//! Render-based previews (model, material) are intentionally NOT re-rendered
//! every frame: they're generated once into a fixed-size raster (see
//! `THUMB_SIZE`) on a cache miss, capped at a few generations per frame
//! (`g_frame_budget`) so opening a folder full of uncached meshes doesn't
//! stall a frame. Texture previews skip the raster pipeline for compressed
//! (KTX2/BCn) source images — those fall back to the type icon.
const std = @import("std");
const gui = @import("gui");
const engine = @import("engine");
const editor = @import("editor");
const EditorState = @import("../../services/EditorState.zig");
const GpuRenderer = @import("../../scene-view/GpuRenderer.zig");
const MeshBounds = @import("MeshBounds.zig");
const PreviewCamera = @import("PreviewCamera.zig");
const PreviewRaster = @import("PreviewRaster.zig");
const Preview3D = @import("Preview3D.zig");

const page = std.heap.page_allocator;

/// Fixed square resolution every generated (rendered or resized) thumbnail is
/// cached at; display size is purely a widget-layout concern (`gui.image`
/// scales the cached texture), so one cached resolution keeps the cache and
/// disk format simple. Revisit if a project's asset browser needs to display
/// thumbnails meaningfully larger than this.
pub const THUMB_SIZE: u32 = 128;

pub const Raster = struct { pixels: []u8, w: u32, h: u32 };

pub const GenCtx = struct {
    guid: []const u8,
    path: []const u8,
    asset_type: editor.AssetType,
};

pub const ProviderFn = *const fn (ctx: GenCtx) ?Raster;

// ── Provider registry ────────────────────────────────────────────────────────

const MAX_PROVIDERS = 16;
const ProviderEntry = struct { asset_type: editor.AssetType, f: ProviderFn };
var providers: [MAX_PROVIDERS]ProviderEntry = undefined;
var provider_count: usize = 0;

/// Register (or replace) the thumbnail generator for `asset_type`. The last
/// registration for a type wins, so a project/plugin can override a built-in
/// previewer (e.g. a custom material preview shape) by registering after
/// `PreviewSystem.init()`.
pub fn registerProvider(asset_type: editor.AssetType, f: ProviderFn) void {
    for (providers[0..provider_count]) |*p| {
        if (p.asset_type == asset_type) {
            p.f = f;
            return;
        }
    }
    if (provider_count >= MAX_PROVIDERS) return;
    providers[provider_count] = .{ .asset_type = asset_type, .f = f };
    provider_count += 1;
}

fn providerFor(asset_type: editor.AssetType) ?ProviderFn {
    for (providers[0..provider_count]) |p| {
        if (p.asset_type == asset_type) return p.f;
    }
    return null;
}

// ── Live/interactive preview registry ───────────────────────────────────────
// Some asset types need more than a static raster: a material's preview edits
// live (unsaved) values, a model's orbits interactively, a font's is vector
// text redrawn every frame. Registering a `LiveDrawFn` here — instead of a
// per-type `if` chain in `Inspector.drawPreviewPanel` — is the same
// data-driven idiom `EditorRegistry`/`registerProvider` already use: the
// Inspector stays a thin host, editors own their own preview.

/// Draws a live/interactive preview inline (dvui calls), given the resolved
/// GUID so implementations don't each re-derive it. Implementations keep
/// their own state (module-level vars), same as `EditorRegistry`'s per-type
/// editors — this is a plain draw callback, not a snapshot generator.
pub const LiveDrawFn = *const fn (asset_path: []const u8, guid: []const u8) void;

const MAX_LIVE_PROVIDERS = 8;
const LiveProviderEntry = struct { asset_type: editor.AssetType, f: LiveDrawFn };
var live_providers: [MAX_LIVE_PROVIDERS]LiveProviderEntry = undefined;
var live_provider_count: usize = 0;

/// Register (or replace) the live preview drawer for `asset_type`.
pub fn registerLiveProvider(asset_type: editor.AssetType, f: LiveDrawFn) void {
    for (live_providers[0..live_provider_count]) |*p| {
        if (p.asset_type == asset_type) {
            p.f = f;
            return;
        }
    }
    if (live_provider_count >= MAX_LIVE_PROVIDERS) return;
    live_providers[live_provider_count] = .{ .asset_type = asset_type, .f = f };
    live_provider_count += 1;
}

/// Whether a live preview drawer is registered for `asset_type`.
pub fn hasLiveProvider(asset_type: editor.AssetType) bool {
    for (live_providers[0..live_provider_count]) |p| {
        if (p.asset_type == asset_type) return true;
    }
    return false;
}

/// Draw the registered live preview for the asset at `asset_path`, if any.
/// Returns whether one was drawn — callers fall back to the static raster
/// path (`imageSourceFor`) when this returns false.
pub fn drawLive(asset_type: editor.AssetType, asset_path: []const u8) bool {
    const f = for (live_providers[0..live_provider_count]) |p| {
        if (p.asset_type == asset_type) break p.f;
    } else return false;

    if (!EditorState.assetDbReady()) return false;
    const info = EditorState.asset_db.findByPath(asset_path) orelse return false;
    var guid_buf: [36]u8 = undefined;
    const guid = info.guid.toString(&guid_buf);
    f(asset_path, guid);
    return true;
}

/// Register the built-in providers (texture / model / material / audio /
/// font). Call once at studio startup, after `GpuRenderer.init`. Live
/// previewers (model/material/font) are registered separately from
/// `studio/Main.zig` right after this call, per `registerLiveProvider`'s doc
/// comment — this function only owns the built-in *raster* providers so it
/// doesn't need to import every editor module.
pub fn init() void {
    provider_count = 0;
    live_provider_count = 0;
    cache_count = 0;
    cache_next = 0;
    g_generation = 0;
    registerProvider(.image, genTexture);
    registerProvider(.model, genModel);
    registerProvider(.material, genMaterial);
    registerProvider(.audio, genAudio);
    registerProvider(.font, genFont);
}

/// Reset the per-frame render-preview generation budget. Call once per frame
/// (e.g. alongside `GpuRenderer.beginFrame`) before any panel requests
/// previews, so a burst of cache misses (a freshly-opened, never-browsed
/// folder) is spread across several frames instead of stalling one.
pub fn beginFrame() void {
    g_frame_budget = FRAME_BUDGET;
}

const FRAME_BUDGET: u32 = 3;
var g_frame_budget: u32 = FRAME_BUDGET;

/// Bumped whenever something *might* have invalidated cached previews in bulk
/// (currently: `AssetWatcher` detecting an external change anywhere under
/// `assets/`). A cache hit only pays for a `.meta` re-read (to compare
/// `source_hash`) when its `checked_gen` is stale relative to this — i.e. at
/// most once per detected-change event, and only for entries actually
/// requested since then. This is what keeps a browser full of thumbnails fast:
/// re-reading every visible asset's `.meta` file every frame (the original,
/// much slower implementation) turned into O(visible tiles × 60/sec) file I/O.
var g_generation: u32 = 0;

pub fn bumpGeneration() void {
    g_generation +%= 1;
}

// ── Public entry point ───────────────────────────────────────────────────────

/// The preview image for the asset at `asset_path`, or null if it has no
/// registered provider / isn't cookable yet / the per-frame generation budget
/// is exhausted (callers should fall back to the type icon and try again next
/// frame). Cheap on a cache hit in the common case (guid lookup only, no I/O).
pub fn imageSourceFor(asset_path: []const u8) ?gui.ImageSource {
    if (!EditorState.assetDbReady()) return null;
    const info = EditorState.asset_db.findByPath(asset_path) orelse return null;
    const pf = providerFor(info.asset_type) orelse return null;

    var guid_buf: [36]u8 = undefined;
    const guid = info.guid.toString(&guid_buf);

    if (find(guid)) |e| {
        if (e.checked_gen == g_generation) return sourceOrNull(e);
        // Something changed somewhere since this entry was last validated —
        // pay for one `.meta` read to check THIS asset specifically, not the
        // whole cache.
        e.checked_gen = g_generation;
        if (readSourceHash(asset_path) == e.source_hash) return sourceOrNull(e);
    }

    const hash = readSourceHash(asset_path);

    if (loadDisk(guid, hash)) |r| {
        return pixelsSource(put(guid, hash, r.pixels, r.w, r.h));
    }

    if (g_frame_budget == 0) return null;
    g_frame_budget -= 1;

    const ctx = GenCtx{ .guid = guid, .path = asset_path, .asset_type = info.asset_type };
    const r = pf(ctx) orelse {
        // Cache the failure too (e.g. a compressed/KTX2 source `genTexture`
        // deliberately skips) — otherwise every visible tile whose provider
        // fails re-attempts the full read-and-decode EVERY frame forever,
        // which is what actually made large asset folders crawl.
        _ = put(guid, hash, &.{}, 0, 0);
        return null;
    };
    saveDisk(guid, hash, r.pixels, r.w, r.h);
    return pixelsSource(put(guid, hash, r.pixels, r.w, r.h));
}

/// `e`'s image, or null if `e` is a cached "provider failed" marker (empty
/// pixels) — callers fall back to the type icon either way.
fn sourceOrNull(e: *const CacheEntry) ?gui.ImageSource {
    return if (e.pixels.len > 0) pixelsSource(e) else null;
}

fn readSourceHash(asset_path: []const u8) u64 {
    var arena_state = std.heap.ArenaAllocator.init(page);
    defer arena_state.deinit();
    return editor.asset_meta.readMeta(gui.io, arena_state.allocator(), asset_path).source_hash;
}

/// The preview image for a *sub-asset* (a material/texture/etc. generated
/// from a model — e.g. a glTF's embedded materials, see `MetaFile.sub_assets`
/// / the Asset Browser's expand arrow on model tiles). Sub-assets live only in
/// the cache and have no `.meta` of their own, so there's no `source_hash` to
/// compare — the cache is trusted by GUID alone until the model that produced
/// it is explicitly reimported (`AssetBrowser`'s "Reimport Asset" invalidates
/// every sub-asset GUID a model lists, right after regenerating them).
pub fn imageSourceForGuid(guid: []const u8, path_for_provider: []const u8, asset_type: editor.AssetType) ?gui.ImageSource {
    const pf = providerFor(asset_type) orelse return null;
    if (find(guid)) |e| return sourceOrNull(e);

    if (loadDisk(guid, 0)) |r| return pixelsSource(put(guid, 0, r.pixels, r.w, r.h));

    if (g_frame_budget == 0) return null;
    g_frame_budget -= 1;

    const ctx = GenCtx{ .guid = guid, .path = path_for_provider, .asset_type = asset_type };
    const r = pf(ctx) orelse {
        _ = put(guid, 0, &.{}, 0, 0); // cache the failure — see `imageSourceFor`.
        return null;
    };
    saveDisk(guid, 0, r.pixels, r.w, r.h);
    return pixelsSource(put(guid, 0, r.pixels, r.w, r.h));
}

/// Drop `guid`'s cached preview (memory + disk) so the next request
/// regenerates it. Call right after a user action that's known to change an
/// asset's rendered output (e.g. `MaterialEditor`'s Save) for instant visual
/// feedback, instead of waiting for the next `bumpGeneration` + lazy re-check.
pub fn invalidate(guid: []const u8) void {
    if (find(guid)) |e| {
        if (e.pixels.len > 0) page.free(e.pixels);
        e.guid_len = 0;
        e.pixels = &.{};
    }
    var path_buf: [1024]u8 = undefined;
    if (diskPath(&path_buf, guid)) |p| std.Io.Dir.cwd().deleteFile(gui.io, p) catch {};
}

fn pixelsSource(e: *const CacheEntry) gui.ImageSource {
    return .{ .pixels = .{ .rgba = e.pixels, .width = e.w, .height = e.h, .invalidation = .ptr } };
}

// ── In-memory cache (guid + source_hash → owned RGBA8 pixels) ───────────────

const CacheEntry = struct {
    guid_buf: [36]u8 = undefined,
    guid_len: usize = 0,
    source_hash: u64 = 0,
    /// `g_generation` value this entry was last validated against — lets a
    /// cache hit skip the `.meta` read entirely most frames (see
    /// `g_generation`'s doc comment).
    checked_gen: u32 = 0,
    pixels: []u8 = &.{},
    w: u32 = 0,
    h: u32 = 0,
};

const MAX_CACHE = 256;
var cache: [MAX_CACHE]CacheEntry = undefined;
var cache_count: usize = 0;
var cache_next: usize = 0; // FIFO eviction cursor once the cache is full

fn find(guid: []const u8) ?*CacheEntry {
    for (cache[0..cache_count]) |*e| {
        if (e.guid_len == guid.len and std.mem.eql(u8, e.guid_buf[0..e.guid_len], guid)) return e;
    }
    return null;
}

fn put(guid: []const u8, hash: u64, pixels: []u8, w: u32, h: u32) *CacheEntry {
    if (find(guid)) |e| {
        if (e.pixels.len > 0) page.free(e.pixels);
        e.source_hash = hash;
        e.checked_gen = g_generation;
        e.pixels = pixels;
        e.w = w;
        e.h = h;
        return e;
    }
    var slot: *CacheEntry = undefined;
    if (cache_count < MAX_CACHE) {
        slot = &cache[cache_count];
        cache_count += 1;
    } else {
        slot = &cache[cache_next];
        if (slot.pixels.len > 0) page.free(slot.pixels);
        cache_next = (cache_next + 1) % MAX_CACHE;
    }
    slot.guid_len = @min(guid.len, slot.guid_buf.len);
    @memcpy(slot.guid_buf[0..slot.guid_len], guid[0..slot.guid_len]);
    slot.source_hash = hash;
    slot.checked_gen = g_generation;
    slot.pixels = pixels;
    slot.w = w;
    slot.h = h;
    return slot;
}

// ── Disk cache (project/.cache/thumbnails/<guid>.thumb) ──────────────────────
// A tiny fixed header (magic/version/source_hash/w/h) followed by raw RGBA8
// bytes — no image codec involved, so writing/reading it needs no dependency
// beyond what's already linked.

const DISK_MAGIC = "TPRV";
const DISK_VERSION: u32 = 1;
const DISK_HEADER_LEN = 24;

fn diskPath(buf: []u8, guid: []const u8) ?[]u8 {
    const proj = EditorState.project_path orelse return null;
    return std.fmt.bufPrint(buf, "{s}/.cache/thumbnails/{s}.thumb", .{ proj, guid }) catch null;
}

fn loadDisk(guid: []const u8, source_hash: u64) ?Raster {
    var path_buf: [1024]u8 = undefined;
    const path = diskPath(&path_buf, guid) orelse return null;
    const bytes = std.Io.Dir.cwd().readFileAlloc(gui.io, path, page, .unlimited) catch return null;
    defer page.free(bytes);
    if (bytes.len < DISK_HEADER_LEN or !std.mem.eql(u8, bytes[0..4], DISK_MAGIC)) return null;
    if (std.mem.readInt(u32, bytes[4..8], .little) != DISK_VERSION) return null;
    if (std.mem.readInt(u64, bytes[8..16], .little) != source_hash) return null;
    const w = std.mem.readInt(u32, bytes[16..20], .little);
    const h = std.mem.readInt(u32, bytes[20..24], .little);
    const need = @as(usize, w) * @as(usize, h) * 4;
    if (bytes.len < DISK_HEADER_LEN + need) return null;
    const pixels = page.alloc(u8, need) catch return null;
    @memcpy(pixels, bytes[DISK_HEADER_LEN .. DISK_HEADER_LEN + need]);
    return .{ .pixels = pixels, .w = w, .h = h };
}

fn saveDisk(guid: []const u8, source_hash: u64, pixels: []const u8, w: u32, h: u32) void {
    const proj = EditorState.project_path orelse return;
    var dir_buf: [1024]u8 = undefined;
    const dir = std.fmt.bufPrint(&dir_buf, "{s}/.cache/thumbnails", .{proj}) catch return;
    std.Io.Dir.cwd().createDirPath(gui.io, dir) catch {};

    const total = DISK_HEADER_LEN + pixels.len;
    const buf = page.alloc(u8, total) catch return;
    defer page.free(buf);
    @memcpy(buf[0..4], DISK_MAGIC);
    std.mem.writeInt(u32, buf[4..8], DISK_VERSION, .little);
    std.mem.writeInt(u64, buf[8..16], source_hash, .little);
    std.mem.writeInt(u32, buf[16..20], w, .little);
    std.mem.writeInt(u32, buf[20..24], h, .little);
    @memcpy(buf[DISK_HEADER_LEN..], pixels);

    var path_buf: [1024]u8 = undefined;
    const path = diskPath(&path_buf, guid) orelse return;
    std.Io.Dir.cwd().writeFile(gui.io, .{ .sub_path = path, .data = buf }) catch {};
}

// ── Built-in providers ────────────────────────────────────────────────────────

fn readSourceBytes(path: []const u8) ?[]u8 {
    return std.Io.Dir.cwd().readFileAlloc(gui.io, path, page, .unlimited) catch null;
}

/// Texture preview: decode the source image and box-resize (nearest-sample) it
/// to fit within a `THUMB_SIZE` square, centered with transparent padding.
/// Compressed (KTX2/BCn) sources are skipped — falls back to the type icon.
fn genTexture(ctx: GenCtx) ?Raster {
    const bytes = readSourceBytes(ctx.path) orelse return null;
    defer page.free(bytes);
    var img = engine.assets.loadTextureFromMemory(page, bytes) catch return null;
    defer img.deinit();
    if (img.isCompressed()) return null;
    const r = PreviewRaster.resizeToThumb(page, img.data, img.width, img.height, THUMB_SIZE) orelse return null;
    return .{ .pixels = r.pixels, .w = r.w, .h = r.h };
}

/// Model preview: render the mesh with the default material, camera
/// auto-framed to its bounds (see `MeshBounds`).
fn genModel(ctx: GenCtx) ?Raster {
    const bounds = MeshBounds.local(ctx.guid) orelse return null;
    const center = engine.Vector3{
        .x = (bounds.min.x + bounds.max.x) * 0.5,
        .y = (bounds.min.y + bounds.max.y) * 0.5,
        .z = (bounds.min.z + bounds.max.z) * 0.5,
    };
    const ext = engine.Vector3{ .x = bounds.max.x - bounds.min.x, .y = bounds.max.y - bounds.min.y, .z = bounds.max.z - bounds.min.z };
    const radius = @sqrt(ext.x * ext.x + ext.y * ext.y + ext.z * ext.z) * 0.5;

    var orbit = PreviewCamera.Orbit{};
    orbit.frame(center, if (radius > 0.001) radius else 0.5);

    const lights = Preview3D.keyFillLights();
    var nodes = [_]engine.SceneNode{ lights[0], lights[1], Preview3D.meshNode(ctx.guid, engine.Material.presets[0].guid) };
    const cap = GpuRenderer.renderAndCapture(page, &nodes, orbit.pose(), THUMB_SIZE, THUMB_SIZE) orelse return null;
    return .{ .pixels = cap.pixels, .w = cap.w, .h = cap.h };
}

/// Material preview: the material applied to a unit sphere (Unity/Godot's
/// default preview shape). `MaterialEditor`'s live panel lets the user swap to
/// a cube and orbit interactively; this static thumbnail always uses the
/// sphere at a fixed pleasant angle.
fn genMaterial(ctx: GenCtx) ?Raster {
    var orbit = PreviewCamera.Orbit{};
    orbit.frame(.{}, Preview3D.primitive_frame_radius);

    const lights = Preview3D.keyFillLights();
    var nodes = [_]engine.SceneNode{ lights[0], lights[1], Preview3D.meshNode(engine.PrimitiveMesh.sphere_guid, ctx.guid) };
    const cap = GpuRenderer.renderAndCapture(page, &nodes, orbit.pose(), THUMB_SIZE, THUMB_SIZE) orelse return null;
    return .{ .pixels = cap.pixels, .w = cap.w, .h = cap.h };
}

/// Audio preview: a peak-amplitude waveform strip. PCM16 `.wav` only for now
/// (no ogg/mp3 decoder in the engine yet) — other formats fall back to the
/// type icon.
fn genAudio(ctx: GenCtx) ?Raster {
    const bytes = readSourceBytes(ctx.path) orelse return null;
    defer page.free(bytes);
    const wav = PreviewRaster.parseWav(bytes) orelse return null;
    const r = PreviewRaster.waveformRaster(page, wav, THUMB_SIZE) orelse return null;
    return .{ .pixels = r.pixels, .w = r.w, .h = r.h };
}

/// Font preview: a big "A" rasterized directly from the font's own glyphs via
/// FreeType, white-on-transparent, centered in the thumb square. Bypasses
/// dvui's live font/atlas system entirely (no `Window`/`Backend` needed, same
/// synchronous-decode style as `genTexture`) — `dvui.ft2lib` is a process-wide
/// FreeType library handle `Window.init` already sets up, so this reuses it
/// rather than initializing a second one. Falls back to the type icon (`null`)
/// on any FreeType error, or if this build doesn't link FreeType at all
/// (`gui.useFreeType == false`, e.g. a future WASM target).
fn genFont(ctx: GenCtx) ?Raster {
    if (comptime !gui.useFreeType) return null;

    const bytes = readSourceBytes(ctx.path) orelse return null;
    defer page.free(bytes);

    var face: gui.c.FT_Face = undefined;
    var args: gui.c.FT_Open_Args = undefined;
    args.flags = @as(u32, @bitCast(gui.Font.FreeType.OpenFlags{ .memory = true }));
    args.memory_base = bytes.ptr;
    args.memory_size = @intCast(bytes.len);
    gui.Font.FreeType.intToError(gui.c.FT_Open_Face(gui.ft2lib, &args, 0, &face)) catch return null;
    defer _ = gui.c.FT_Done_Face(face);

    const px: u32 = THUMB_SIZE - 32; // leave a margin so ascenders/descenders don't clip
    gui.Font.FreeType.intToError(gui.c.FT_Set_Pixel_Sizes(face, px, px)) catch return null;
    gui.Font.FreeType.intToError(gui.c.FT_Load_Char(face, 'A', @bitCast(gui.Font.FreeType.LoadFlags{ .render = true }))) catch return null;

    const bitmap = face.*.glyph.*.bitmap;
    if (bitmap.width == 0 or bitmap.rows == 0) return null;

    const pixels = page.alloc(u8, THUMB_SIZE * THUMB_SIZE * 4) catch return null;
    @memset(pixels, 0);

    const gw: i32 = @intCast(bitmap.width);
    const gh: i32 = @intCast(bitmap.rows);
    const ox = @divTrunc(@as(i32, THUMB_SIZE) - gw, 2);
    const oy = @divTrunc(@as(i32, THUMB_SIZE) - gh, 2);

    var row: i32 = 0;
    while (row < gh) : (row += 1) {
        const dy = oy + row;
        if (dy < 0 or dy >= THUMB_SIZE) continue;
        var col: i32 = 0;
        while (col < gw) : (col += 1) {
            const dx = ox + col;
            if (dx < 0 or dx >= THUMB_SIZE) continue;
            const src = bitmap.buffer[@intCast(row * bitmap.pitch + col)];
            const di = (@as(usize, @intCast(dy)) * THUMB_SIZE + @as(usize, @intCast(dx))) * 4;
            pixels[di + 0] = 255;
            pixels[di + 1] = 255;
            pixels[di + 2] = 255;
            pixels[di + 3] = src;
        }
    }
    return .{ .pixels = pixels, .w = THUMB_SIZE, .h = THUMB_SIZE };
}
