namespace Turian.Tests;

/// <summary>Checks optional editor validation reporting without invoking scene callbacks.</summary>
public sealed class UserCodeValidationTests
{
    sealed class Validator : IUserCodeValidator
    {
        public IEnumerable<UserCodeValidationWarning> Validate(string assetsDirectory)
            => [new(Path.Combine(assetsDirectory, "Source.cs"), 12, "validation warning")];
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
