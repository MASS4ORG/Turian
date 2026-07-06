//! Desktop (SDL3) platform backend for the game-code generator.
//!
//! GameBuild.zig emits a standalone game `main.zig`. The platform-specific
//! pieces — windowing, the event pump, input device bindings — were previously
//! inlined as string literals inside GameBuild. They are collected here so the
//! generator's platform layer lives in one place and so additional backends
//! (Android, iOS, consoles — see item 8) can be added as sibling
//! modules exposing the same `pub const` source-fragment interface.
//!
//! These fragments are SDL3-ABI exact (field offsets/enum values) and are not
//! CPU-architecture specific despite the historical "x64-only" note — the real
//! platform variance is the windowing/input API, which is what this abstracts.

/// SDL3 windowing + 2D renderer bindings (software-blit present path).
pub const bindings =
    "// SDL3 bindings\n" ++
    "const SDL_Window   = opaque {};\n" ++
    "const SDL_Renderer = opaque {};\n" ++
    "const SDL_Texture  = opaque {};\n" ++
    "const SDL_INIT_VIDEO: u32            = 0x00000020;\n" ++
    "const SDL_EVENT_QUIT: u32            = 0x100;\n" ++
    "const SDL_PIXELFORMAT_ABGR8888: u32  = 0x16762004;\n" ++
    "const SDL_TEXTUREACCESS_STREAMING: c_int = 1;\n" ++
    "const SDL_Event = extern struct { type: u32, padding: [124]u8 = undefined };\n" ++
    "extern fn SDL_Init(flags: u32) bool;\n" ++
    "extern fn SDL_CreateWindow(title: [*:0]const u8, w: c_int, h: c_int, flags: u64) ?*SDL_Window;\n" ++
    "extern fn SDL_DestroyWindow(w: *SDL_Window) void;\n" ++
    "extern fn SDL_PollEvent(e: *SDL_Event) bool;\n" ++
    "extern fn SDL_Quit() void;\n" ++
    "extern fn SDL_Delay(ms: u32) void;\n" ++
    "extern fn SDL_CreateRenderer(w: *SDL_Window, name: ?[*:0]const u8) ?*SDL_Renderer;\n" ++
    "extern fn SDL_DestroyRenderer(r: *SDL_Renderer) void;\n" ++
    "extern fn SDL_SetRenderVSync(r: *SDL_Renderer, vsync: c_int) bool;\n" ++
    "extern fn SDL_RenderClear(r: *SDL_Renderer) bool;\n" ++
    "extern fn SDL_RenderPresent(r: *SDL_Renderer) bool;\n" ++
    "extern fn SDL_CreateTexture(r: *SDL_Renderer, fmt: u32, access: c_int, w: c_int, h: c_int) ?*SDL_Texture;\n" ++
    "extern fn SDL_DestroyTexture(t: *SDL_Texture) void;\n" ++
    "extern fn SDL_UpdateTexture(t: *SDL_Texture, rect: ?*const anyopaque, pixels: *const anyopaque, pitch: c_int) bool;\n" ++
    "extern fn SDL_RenderTexture(r: *SDL_Renderer, t: *SDL_Texture, src: ?*const anyopaque, dst: ?*const anyopaque) bool;\n\n";

/// SDL3 keyboard/mouse event overlays + scancode mapping feeding engine.Input
///. Also declares the global Input/Services instances.
pub const input =
    "const SDL_EVENT_KEY_DOWN: u32          = 0x300;\n" ++
    "const SDL_EVENT_KEY_UP: u32            = 0x301;\n" ++
    "const SDL_EVENT_MOUSE_MOTION: u32      = 0x400;\n" ++
    "const SDL_EVENT_MOUSE_BUTTON_DOWN: u32 = 0x401;\n" ++
    "const SDL_EVENT_MOUSE_BUTTON_UP: u32   = 0x402;\n" ++
    "const SDL_EVENT_MOUSE_WHEEL: u32       = 0x403;\n" ++
    "const SDL_KeyboardEvent = extern struct { type: u32, reserved: u32, timestamp: u64, windowID: u32, which: u32, scancode: u32, key: u32, mod: u16, raw: u16, down: bool, repeat: bool };\n" ++
    "const SDL_MouseButtonEvent = extern struct { type: u32, reserved: u32, timestamp: u64, windowID: u32, which: u32, button: u8, down: bool, clicks: u8, pad: u8, x: f32, y: f32 };\n" ++
    "const SDL_MouseMotionEvent = extern struct { type: u32, reserved: u32, timestamp: u64, windowID: u32, which: u32, state: u32, x: f32, y: f32, xrel: f32, yrel: f32 };\n" ++
    "const SDL_MouseWheelEvent = extern struct { type: u32, reserved: u32, timestamp: u64, windowID: u32, which: u32, x: f32, y: f32, direction: u32, mouse_x: f32, mouse_y: f32 };\n\n" ++
    "var g_input: engine.Input = engine.Input.init();\n" ++
    "var g_services: engine.Services = engine.Services.init();\n" ++
    "var g_application: engine.Application = .{};\n\n" ++
    "fn scancodeToKey(sc: u32) ?engine.Key {\n" ++
    "    return switch (sc) {\n" ++
    "        4...29 => @enumFromInt(@intFromEnum(engine.Key.a) + (sc - 4)),\n" ++
    "        30...38 => @enumFromInt(@intFromEnum(engine.Key.num_1) + (sc - 30)),\n" ++
    "        39 => .num_0,\n" ++
    "        40 => .enter, 41 => .escape, 42 => .backspace, 43 => .tab, 44 => .space,\n" ++
    "        79 => .right, 80 => .left, 81 => .down, 82 => .up,\n" ++
    "        224 => .left_ctrl, 225 => .left_shift, 226 => .left_alt,\n" ++
    "        228 => .right_ctrl, 229 => .right_shift, 230 => .right_alt,\n" ++
    "        else => null,\n" ++
    "    };\n" ++
    "}\n\n" ++
    "fn sdlButtonToMouse(b: u8) ?engine.MouseButton {\n" ++
    "    return switch (b) { 1 => .left, 2 => .middle, 3 => .right, 4 => .x1, 5 => .x2, else => null };\n" ++
    "}\n\n";

/// SDL3 gamepad events feeding engine.Input. SDL's
/// SDL_GamepadButton/SDL_GamepadAxis enums share engine.GamepadButton/Axis order.
pub const gamepad =
    "const SDL_INIT_GAMEPAD: u32                 = 0x00002000;\n" ++
    "const SDL_EVENT_GAMEPAD_AXIS_MOTION: u32    = 0x650;\n" ++
    "const SDL_EVENT_GAMEPAD_BUTTON_DOWN: u32    = 0x651;\n" ++
    "const SDL_EVENT_GAMEPAD_BUTTON_UP: u32      = 0x652;\n" ++
    "const SDL_EVENT_GAMEPAD_ADDED: u32          = 0x653;\n" ++
    "const SDL_Gamepad = opaque {};\n" ++
    "const SDL_GamepadButtonEvent = extern struct { type: u32, reserved: u32, timestamp: u64, which: u32, button: u8, down: bool, p1: u8, p2: u8 };\n" ++
    "const SDL_GamepadAxisEvent = extern struct { type: u32, reserved: u32, timestamp: u64, which: u32, axis: u8, p1: u8, p2: u8, p3: u8, value: i16, p4: u16 };\n" ++
    "const SDL_GamepadDeviceEvent = extern struct { type: u32, reserved: u32, timestamp: u64, which: u32 };\n" ++
    "extern fn SDL_OpenGamepad(id: u32) ?*SDL_Gamepad;\n\n" ++
    "fn sdlPadButton(b: u8) ?engine.GamepadButton {\n" ++
    "    return if (b < 15) @enumFromInt(b) else null;\n" ++
    "}\n" ++
    "fn sdlPadAxis(ax: u8) ?engine.GamepadAxis {\n" ++
    "    return if (ax < 6) @enumFromInt(ax) else null;\n" ++
    "}\n\n";

/// Applies one SDL event to `g_input`. `ui_mouse`/`ui_key` say whether the
/// in-game GUI consumed the pointer / keyboard this frame (dvui set `.handled`
/// on those events); when set, the corresponding button/wheel/key presses are
/// withheld from world input so a click on a UI widget doesn't also reach
/// gameplay — the shipped-game half of the input-priority rule Studio's
/// `PlayMode.feedInput` already applies. Mouse *motion* and all gamepad input
/// are never suppressed (a cursor merely passing over a HUD must not freeze a
/// live camera, and UI does not consume gamepad events yet — post-MVP nav).
pub const apply_input =
    "fn applyInputEvent(ev: *align(8) const SDL_Event, ui_mouse: bool, ui_key: bool) void {\n" ++
    "    switch (ev.type) {\n" ++
    "        SDL_EVENT_KEY_DOWN, SDL_EVENT_KEY_UP => {\n" ++
    "            if (ui_key) return;\n" ++
    "            const ke: *const SDL_KeyboardEvent = @ptrCast(ev);\n" ++
    "            if (scancodeToKey(ke.scancode)) |k| g_input.setKey(k, ev.type == SDL_EVENT_KEY_DOWN);\n" ++
    "        },\n" ++
    "        SDL_EVENT_MOUSE_MOTION => {\n" ++
    "            const me: *const SDL_MouseMotionEvent = @ptrCast(ev);\n" ++
    "            g_input.setMousePosition(me.x, me.y);\n" ++
    "            g_input.addMouseMotion(me.xrel, me.yrel);\n" ++
    "        },\n" ++
    "        SDL_EVENT_MOUSE_BUTTON_DOWN, SDL_EVENT_MOUSE_BUTTON_UP => {\n" ++
    "            if (ui_mouse) return;\n" ++
    "            const be: *const SDL_MouseButtonEvent = @ptrCast(ev);\n" ++
    "            if (sdlButtonToMouse(be.button)) |mb| g_input.setMouseButton(mb, ev.type == SDL_EVENT_MOUSE_BUTTON_DOWN);\n" ++
    "        },\n" ++
    "        SDL_EVENT_MOUSE_WHEEL => {\n" ++
    "            if (ui_mouse) return;\n" ++
    "            const we: *const SDL_MouseWheelEvent = @ptrCast(ev);\n" ++
    "            g_input.addWheel(we.y);\n" ++
    "        },\n" ++
    "        SDL_EVENT_GAMEPAD_ADDED => {\n" ++
    "            const ge: *const SDL_GamepadDeviceEvent = @ptrCast(ev);\n" ++
    "            _ = SDL_OpenGamepad(ge.which);\n" ++
    "            g_input.gamepad_connected = true;\n" ++
    "        },\n" ++
    "        SDL_EVENT_GAMEPAD_BUTTON_DOWN, SDL_EVENT_GAMEPAD_BUTTON_UP => {\n" ++
    "            const be: *const SDL_GamepadButtonEvent = @ptrCast(ev);\n" ++
    "            if (sdlPadButton(be.button)) |pb| g_input.setGamepadButton(pb, ev.type == SDL_EVENT_GAMEPAD_BUTTON_DOWN);\n" ++
    "        },\n" ++
    "        SDL_EVENT_GAMEPAD_AXIS_MOTION => {\n" ++
    "            const ae: *const SDL_GamepadAxisEvent = @ptrCast(ev);\n" ++
    "            if (sdlPadAxis(ae.axis)) |pa| {\n" ++
    "                const norm = @as(f32, @floatFromInt(ae.value)) / 32767.0;\n" ++
    "                g_input.setGamepadAxis(pa, std.math.clamp(norm, -1.0, 1.0));\n" ++
    "            }\n" ++
    "        },\n" ++
    "        else => {},\n" ++
    "    }\n" ++
    "}\n\n";
