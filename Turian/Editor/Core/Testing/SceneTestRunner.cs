using Turian.Testing;

namespace Turian.Editor.Core;

/// <summary>Discovers and runs component tests sequentially with a fresh headless scene per method.</summary>
public static class SceneTestRunner
{
    /// <summary>Lists or executes annotated methods, optionally selecting names by a case-insensitive substring.</summary>
    /// <param name="assemblies">The user and test assemblies to inspect.</param>
    /// <param name="filter">Part of a fully qualified test name.</param>
    /// <param name="listOnly">Whether to list tests without constructing fixtures or scenes.</param>
    /// <param name="cancellationToken">Stops before the next test; synchronous methods must return cooperatively.</param>
    /// <param name="symbolsDirectory">Where symbols for in-memory assemblies are stored.</param>
    public static SceneTestReport Run(IEnumerable<ReflectionAssembly> assemblies, string? filter = null,
        bool listOnly = false, CancellationToken cancellationToken = default, string? symbolsDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(assemblies);
        var results = new List<SceneTestResult>();
        foreach (var method in Discover(assemblies, filter))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Name(method);
            var location = SceneTestSource.Read(method, symbolsDirectory);
            results.Add(listOnly
                ? new SceneTestResult(name, SceneTestStatus.NotRun, SourceFile: location.File, SourceLine: location.Line)
                : Execute(method, location));
        }
        return new SceneTestReport(results);
    }

    static IEnumerable<MethodInfo> Discover(IEnumerable<ReflectionAssembly> assemblies, string? filter) =>
        assemblies.SelectMany(static assembly => assembly.GetTypes())
            .SelectMany(static type => type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            .Where(static method => method.IsDefined(typeof(SceneTestAttribute), inherit: false))
            .Where(method => string.IsNullOrEmpty(filter) || Name(method).Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(Name, StringComparer.Ordinal);

    static string Name(MethodInfo method) => $"{method.DeclaringType!.FullName}.{method.Name}";

    static SceneTestResult Execute(MethodInfo method, (string? File, int Line) location)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            Validate(method);
            using var scene = new TestScene();
            var fixture = method.IsStatic ? null : Activator.CreateInstance(method.DeclaringType!, nonPublic: true);
            try { method.Invoke(fixture, method.GetParameters().Length == 0 ? null : [scene]); }
            finally { (fixture as IDisposable)?.Dispose(); }
        }
        catch (Exception error)
        {
            return Failure(method, error, location, timer.Elapsed.TotalSeconds);
        }
        return new SceneTestResult(Name(method), SceneTestStatus.Passed, timer.Elapsed.TotalSeconds,
            SourceFile: location.File, SourceLine: location.Line);
    }

    static SceneTestResult Failure(MethodInfo method, Exception error, (string? File, int Line) location, double seconds)
    {
        while (error is TargetInvocationException { InnerException: { } inner }) { error = inner; }
        var frame = new StackTrace(error, fNeedFileInfo: true).GetFrames()
            .FirstOrDefault(frame => frame.GetMethod()?.DeclaringType?.Assembly == method.DeclaringType!.Assembly
                && frame.GetFileLineNumber() > 0);
        return new SceneTestResult(Name(method), SceneTestStatus.Failed, seconds,
            error.Message, error.ToString(), frame?.GetFileName() ?? location.File,
            frame?.GetFileLineNumber() ?? location.Line);
    }

    static void Validate(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (method.ContainsGenericParameters || method.ReturnType != typeof(void)
            || method.IsDefined(typeof(AsyncStateMachineAttribute), inherit: false)
            || parameters.Length > 1 || parameters.Length == 1 && parameters[0].ParameterType != typeof(TestScene))
        {
            throw new InvalidOperationException("Scene tests must be synchronous void methods with no parameters or one TestScene parameter.");
        }
    }
}
