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
dotnet run --project Turian/Editor/Studio/Turian.Editor.Studio.csproj -- --project path/to/example-01
```

### 4. Headless diagnostics
```bash
dotnet run --project Turian/Editor/CLI -- screenshot path/to/example-01/ --out shot.png
```

## NuGet releases

`./build.sh PackNuGet --configuration Release --no-local-packages --skip-shaders` builds the solution and writes every project declaring a `PackageId` to `artifacts/packages/`: `MASS4.Bricks`, `MASS4.Gaya.Attributes`, `MASS4.Gaya.Sdk`, `MASS4.Turian.Attributes` and `MASS4.Turian.Engine`. `MASS4.Turian.Engine` carries the component source generator under `analyzers/dotnet/cs`. The Gaya workbench, the Bricks panel, the Turian editor and the Vulkan backend ship as DLLs or bricks and are marked non-packable.

`publish-on-tag.yml` calls `PushNuGet` for release tags when `RELEASE_REPOSITORY` is set. Configure a nuget.org Trusted Publishing policy for repository owner `MASS4ORG`, repository `Turian` and workflow `publish-on-tag.yml`, and set the repository secret `NUGET_USER` to the owning nuget.org account name. The workflow exchanges its OIDC token for a short-lived API key and uploads the generated packages as the `nuget-packages` artifact. Existing package versions are skipped on retries.

## Troubleshooting

- **Shader Compilation Failed**: Ensure `glslc` is in your PATH.
- **Vulkan Device Lost**: Ensure your drivers support Vulkan 1.3.

## Generated game dependencies

Building `Turian.Editor.Core` builds the launcher and generates `obj/<configuration>/<framework>/Turian.RuntimeDependencies.txt` with resolved framework, runtime, and native package versions plus assembly references, embedding it in the editor.

`BuildAppSettings` uses this snapshot for project generation. It uses the launcher graph (excluding build-only packages), keeps local paths relative, and resolves external assemblies from launcher output. Published editors load internal assemblies from `lib/`. The list contains resolved runtime packages (including transitive and native dependencies). Release packaging copies runtime assemblies to `lib/` using the same graph.
