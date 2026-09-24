namespace Turian.Editor.Core;

/// <summary>
/// Contract for all user-code build operations.
/// </summary>
public interface IUserCodeCompiler
{
    /// <summary>Executes the operation and returns a result path.</summary>
    Task<string> ExecuteAsync();
}
