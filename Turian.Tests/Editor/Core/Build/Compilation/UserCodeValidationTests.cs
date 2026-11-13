namespace Turian.Tests;

/// <summary>Checks optional editor validation reporting without invoking scene callbacks.</summary>
public sealed class UserCodeValidationTests
{
    sealed class Validator : IUserCodeValidator
    {
        public IEnumerable<UserCodeValidationWarning> Validate(string assetsDirectory)
            => [new(Path.Combine(assetsDirectory, "Source.cs"), 12, "validation warning")];
    }

    sealed class FakeAssembly(string name, Func<UserCodeValidatorAttribute[]> attributes) : Assembly
    {
        public override bool IsCollectible => false;

        public override AssemblyName GetName(bool copiedName) => new(name);

        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => attributes();
    }

    /// <summary>A precast assembly whose dependency is not compiled yet is skipped; other validators still run.</summary>
    [Fact]
    public void SkipsAssembliesWhoseAttributesCannotLoad()
    {
        Assembly[] assemblies =
        [
            new FakeAssembly("Precast.Bepu", static () => throw new FileNotFoundException("Turian.Engine.Physics")),
            new FakeAssembly("Editor.Brick", static () => [new UserCodeValidatorAttribute(typeof(Validator))]),
        ];
        var validators = UserCodeValidation.Validators(assemblies, Substitute.For<ILogger>()).ToList();
        Assert.IsType<Validator>(Assert.Single(validators));
    }

    /// <summary>Optional brick warnings reach Studio's shared logger with a source location.</summary>
    [Fact]
    public void ReportsWarningsWithSourceLocation()
    {
        var logger = Substitute.For<ILogger>();
        UserCodeValidation.Report("Assets", logger, [new Validator()]);
        var call = Assert.Single(logger.ReceivedCalls());
        Assert.Equal(LogLevel.Warning, call.GetArguments()[0]);
        Assert.Contains("Source.cs(12)", call.GetArguments()[2]!.ToString());
        Assert.Contains("validation warning", call.GetArguments()[2]!.ToString());
    }
}
