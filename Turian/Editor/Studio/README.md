# Turian Studio

The desktop entry point that hosts Turian's editor plugin in Gaya.

From the repository root:

```sh
dotnet run --project Turian/Editor/Studio -- --project ../Turian-examples/example-01-basic-project
```

Requires .NET 10 and Vulkan. See the [build guide](../../../docs/Building-from-source.md).

Logging defaults to Information in all configurations. Use `--log-level Debug` or `--log-level Trace` for diagnostics; other accepted levels are Information, Warning, Error, Critical, and None.

Set `GAYA_CONFIG_HOME` to a separate directory for isolated preferences and layouts during testing or headless checks. The default remains `~/.gaya`.
