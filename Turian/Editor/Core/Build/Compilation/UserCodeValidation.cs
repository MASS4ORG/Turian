namespace Turian.Editor.Core;

/// <summary>A source or scene validation warning emitted before compiling or running user code.</summary>
public sealed record UserCodeValidationWarning(string File, int Line, string Message);

/// <summary>Lets optional editor bricks validate source and scene wiring without entering Play Mode.</summary>
public interface IUserCodeValidator
{
    /// <summary>Checks editable project assets and returns warnings with source locations.</summary>
    IEnumerable<UserCodeValidationWarning> Validate(string assetsDirectory);
}

/// <summary>Registers a parameterless validator supplied by an editor assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class UserCodeValidatorAttribute(Type validatorType) : Attribute
{
    /// <summary>The editor validator implementation.</summary>
    public Type ValidatorType { get; } = validatorType;
}

/// <summary>Runs installed editor validators before compilation, including when compiled output is cached.</summary>
public static class UserCodeValidation
{
    /// <summary>Reports installed validators' warnings to Studio's shared Output log.</summary>
    public static void Report(string assetsDirectory, ILogger logger)
    {
        var validators = AppDomain.CurrentDomain.GetAssemblies()
            .Where(static assembly => !assembly.IsDynamic && !BrickAssemblies.IsRetired(assembly))
            .SelectMany(static assembly => assembly.GetCustomAttributes<UserCodeValidatorAttribute>())
            .Select(static attribute => (IUserCodeValidator)Activator.CreateInstance(attribute.ValidatorType)!);
        Report(assetsDirectory, logger, validators);
    }

    internal static void Report(string assetsDirectory, ILogger logger, IEnumerable<IUserCodeValidator> validators)
    {
        foreach (var validator in validators)
        {
            foreach (var warning in validator.Validate(assetsDirectory))
            {
                logger.LogWarning("{File}({Line}): {ValidationMessage}", warning.File, warning.Line, warning.Message);
            }
        }
    }
}
