# Turian tests

Engine, editor, Gaya and brick integration tests, using xUnit and Microsoft.Testing.Platform.

Run from the repository root with .NET 10:

```sh
dotnet run --project Turian.Tests/Turian.Tests.csproj
```

Headless Vulkan tests skip when a usable device is unavailable. Test assets live in `Fixtures/`.
