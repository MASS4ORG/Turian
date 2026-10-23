namespace Turian.Tests;

/// <summary>Tests registering a compiled project's type manifest after its assemblies load.</summary>
public sealed class UserCodeTypeManifestTests : IDisposable
{
    sealed class PrecastStandIn : IdObject;

    readonly string directory = Directory.CreateTempSubdirectory("turian-usercode-manifest-").FullName;

    /// <inheritdoc />
    public void Dispose() => Directory.Delete(directory, recursive: true);

    /// <summary>
    /// A precast brick type, in an assembly the editor already loaded rather than in the user's, registers; a user-code
    /// swap resets the registry, so this is how it comes back. Only entries found nowhere warn.
    /// </summary>
    [Fact]
    public void BrickTypesInLoadedAssembliesRegisterAfterAReset()
    {
        var brickId = Guid.NewGuid();
        var unnamedId = Guid.NewGuid();
        var unloadedId = Guid.NewGuid();
        var assembly = Path.Combine(directory, "game.dll");
        UserCodeTypeManifest.Save(new UserCodeTypeManifest
        {
            Types =
            [
                new UserCodeTypeEntry
                {
                    FullyQualifiedName = typeof(PrecastStandIn).FullName!, TypeId = brickId,
                    Assembly = typeof(PrecastStandIn).Assembly.GetName().Name,
                },
                new UserCodeTypeEntry { FullyQualifiedName = typeof(PrecastStandIn).FullName!, TypeId = unnamedId },
                new UserCodeTypeEntry
                {
                    FullyQualifiedName = typeof(PrecastStandIn).FullName!, TypeId = unloadedId, Assembly = "Turian.Not.Loaded",
                },
            ],
        }, assembly);
        var logger = new WarningLogger();

        UserCodeTypeManifest.RegisterFromManifest(assembly, [], logger);

        Assert.True(TypeRegistry.TryGetType(brickId, out var type));
        Assert.Equal(typeof(PrecastStandIn), type);
        Assert.False(TypeRegistry.TryGetType(unnamedId, out _));
        Assert.False(TypeRegistry.TryGetType(unloadedId, out _));
        Assert.Equal(2, logger.Warnings);
    }

    sealed class WarningLogger : ILogger
    {
        /// <summary>How many warnings were logged.</summary>
        public int Warnings { get; private set; }

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings++;
            }
        }
    }
}
