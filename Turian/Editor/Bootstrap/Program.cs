using System.Reflection;
using System.Runtime.Loader;

var lib = Path.Combine(AppContext.BaseDirectory, "lib");
AssemblyLoadContext.Default.Resolving += (_, name) => TryLoad(name.Name);
var entry = Path.GetFileNameWithoutExtension(Environment.ProcessPath) switch
{
    "turian-cli" => "Turian.Editor.CLI",
    "turian-studio" => "Turian.Editor.Studio",
    _ => throw new InvalidOperationException("Unknown Turian launcher.")
};
var result = (TryLoad(entry) ?? throw new FileNotFoundException($"Could not load {entry} from {lib}."))
    .EntryPoint?.Invoke(null, [args]);
return result switch { Task<int> task => await task, Task task => await Await(task), int code => code, _ => 0 };

Assembly? TryLoad(string? name)
{
    var path = Path.Combine(lib, $"{name}.dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
}

static async Task<int> Await(Task task) { await task; return 0; }
