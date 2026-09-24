# Building from Source

This guide explains how to build Turian on your local machine.

## Prerequisites

1. **.NET 10 SDK**: [Download here](https://dotnet.microsoft.com/download/dotnet/10.0).
2. **Vulkan SDK**: Required for shader compilation and development.
3. **glslc**: Part of the Vulkan SDK or via package manager:
   - Ubuntu: `sudo apt-get install glslc`
   - Windows: Included in Vulkan SDK.

## Build Steps

Turian uses [Nuke Build](https://nuke.build/) for orchestration.

### 1. Compile (Default)
```bash
./build.sh compile
```
This will:
- Restore dependencies
- Compile GLSL shaders to SPIR-V
- Build all C# projects

### 2. Run Tests
```bash
./build.sh Restore Compile TestReport
```

### 3. Run the Studio
To launch the editor with an example project:
```bash
dotnet run --project Turian/Editor/Studio/Turian.Editor.Studio.csproj -- --project ../turian-examples/example-01
```

### 4. Headless diagnostics
```bash
dotnet run --project Turian/Editor/CLI -- screenshot ../turian-examples/example-01/ --out shot.png
```

## Troubleshooting

- **Shader Compilation Failed**: Ensure `glslc` is in your PATH.
- **Vulkan Device Lost**: Ensure your drivers support Vulkan 1.3.
