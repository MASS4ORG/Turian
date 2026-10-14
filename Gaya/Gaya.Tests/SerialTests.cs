namespace Gaya.Tests;

/// <summary>
/// The test classes that touch process-wide state — the current workbench theme, environment variables and static
/// caches — run one at a time in this collection; every other class runs in parallel.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialTests
{
    /// <summary>The collection name test classes opt into with <c>[Collection(SerialTests.Name)]</c>.</summary>
    public const string Name = "Serial";
}
