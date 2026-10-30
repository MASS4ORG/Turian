using MASS4.Bricks;

namespace Turian.Editor.CLI;

public static partial class Program
{
    static Command BrickMaterializeCommand()
    {
        var sourceArgument = new Argument<string>("source")
        {
            Description = "<id>@<exact-version>, typed store/git/archive JSON, or legacy GitHub dependency JSON",
        };
        var pathOption = new Option<DirectoryInfo?>("--path") { Description = "Destination producer folder" };
        var rootOption = new Option<DirectoryInfo>("--root")
        {
            Description = "Parent of the JSON path or default brick-id folder",
            DefaultValueFactory = _ => new DirectoryInfo("Bricks"),
        };
        var storeOption = new Option<string>("--store")
        {
            Description = "Store origin or registry URL",
            DefaultValueFactory = _ => "https://bricks.mass4.org",
        };
        var variantOption = new Option<string>("--variant")
        {
            Description = "Producer variant to materialize",
            DefaultValueFactory = _ => PackageVariant.Source,
        };
        variantOption.AcceptOnlyFromAmong(PackageVariant.Source);
        var keysOption = new Option<string[]>("--key")
        {
            Description = "Trusted store public key or .pub file; repeat or separate keys with newlines",
        };
        var command = new Command("materialize", "Fetch a pinned brick producer layout into a writable folder")
        {
            sourceArgument, pathOption, rootOption, storeOption, variantOption, keysOption,
        };
        command.SetAction(async (result, cancellationToken) =>
        {
            try
            {
                var source = BrickMaterializationSource.Parse(result.GetValue(sourceArgument)!);
                var destination = result.GetValue(pathOption)?.FullName
                    ?? Path.Combine(result.GetValue(rootOption)!.FullName, source.Path ?? source.Id
                        ?? throw new PackageException("A git or archive dependency needs a destination path."));
                var registry = MaterializationRegistry(source, result.GetValue(storeOption)!, result.GetValue(keysOption) ?? []);
                var materializer = new BrickMaterializer(reservedCategoryPrefixes: ["gaya", "turian"]);
                var folder = await materializer.MaterializeAsync(source, destination, registry,
                    Environment.GetEnvironmentVariable("SOURCE_DEP_TOKEN"), cancellationToken).ConfigureAwait(false);
                Console.WriteLine($"Materialized {source.Id ?? source.Type} at {folder}");
                return 0;
            }
            catch (Exception ex) when (ex is PackageException or IOException or UnauthorizedAccessException
                                       or ArgumentException)
            {
                var message = ex.Message.Replace("%", "%25", StringComparison.Ordinal)
                    .Replace("\r", "%0D", StringComparison.Ordinal).Replace("\n", "%0A", StringComparison.Ordinal);
                Console.Error.WriteLine($"::error::{message}");
                return 1;
            }
        });
        return command;
    }

    static ScopedRegistry? MaterializationRegistry(BrickMaterializationSource source, string store, string[] trustedKeys)
    {
        if (source.Type != "store")
        {
            return null;
        }

        var origin = StoreClient.OriginOf(source.StoreUrl ?? store);
        var url = new Uri(origin, "v1").ToString();
        string[] keys = [.. trustedKeys.SelectMany(static key =>
            (File.Exists(key) ? File.ReadAllText(key) : key)
            .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))];
        return url == ProjectPackages.PublicRegistry.Url && keys.Length == 0
            ? ProjectPackages.PublicRegistry
            : new ScopedRegistry { Name = origin.Host, Url = url, Keys = [.. keys] };
    }
}
