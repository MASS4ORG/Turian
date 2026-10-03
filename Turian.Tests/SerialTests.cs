namespace Turian.Tests;

/// <summary>
/// The test classes that touch process-wide state — a Vulkan device, environment variables, the generated-serializer
/// registry, the current Studio theme, static content caches and counters — run one at a time in this collection;
/// every other class runs in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTests
{
    /// <summary>The collection name test classes opt into with <c>[Collection(SerialTests.Name)]</c>.</summary>
    public const string Name = "Serial";
}
