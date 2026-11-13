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
        => Report(assetsDirectory, logger, Validators(AppDomain.CurrentDomain.GetAssemblies(), logger));

    /// <summary>Creates the validators the assemblies declare, skipping assemblies whose attributes cannot load yet.</summary>
    internal static IEnumerable<IUserCodeValidator> Validators(IEnumerable<ReflectionAssembly> assemblies, ILogger logger)
        => assemblies
            .Where(static assembly => !assembly.IsDynamic && !BrickAssemblies.IsRetired(assembly))
            .SelectMany(assembly => ValidatorAttributes(assembly, logger))
            .Select(static attribute => (IUserCodeValidator)Activator.CreateInstance(attribute.ValidatorType)!);

    // A precast brick assembly can reference a brick that is compiled from source only after this check.
    static IEnumerable<UserCodeValidatorAttribute> ValidatorAttributes(ReflectionAssembly assembly, ILogger logger)
    {
        try
        {
            return assembly.GetCustomAttributes<UserCodeValidatorAttribute>();
        }
        catch (Exception exception) when (exception is FileNotFoundException or FileLoadException or TypeLoadException)
        {
            logger.LogDebug("Skipped validators of {Assembly}: {Reason}", assembly.GetName().Name, exception.Message);
            return [];
        }
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
