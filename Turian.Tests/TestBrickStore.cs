namespace Turian.Tests;

/// <summary>
/// Points the shared brick store at a temporary folder for the whole test run, so fixture bricks never reach the
/// user's own store.
/// </summary>
static class TestBrickStore
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var store = Directory.CreateTempSubdirectory("turian-test-bricks-").FullName;
        Environment.SetEnvironmentVariable(PackageStore.StoreVariable, store);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Delete(store);
    }

    static void Delete(string store)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(store, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(store, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A child process may still hold a file; the system cleans its temporary folder eventually.
        }
    }
}
