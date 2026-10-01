# Turian CLI

Headless tools for project compilation, asset import, builds, scene diagnostics and brick distribution.

From the repository root:

```sh
dotnet run --project Turian/Editor/CLI -- --help
```

Requires .NET 10. Rendering commands also require Vulkan. Commands share their implementation with Studio through [Editor Core](../Core/).
