namespace Turian.Engine.UI;

/// <summary>
/// Resolves and writes binding paths against a data source. Re-read each frame (the immediate-mode
/// contract), so a plain POCO works — <c>INotifyPropertyChanged</c> is not required, though a
/// source that raises it composes fine.
/// </summary>
public sealed class BindingContext
{
    /// <summary>Creates a context over <paramref name="source"/>.</summary>
    /// <param name="source">The object binding paths are resolved against.</param>
    public BindingContext(object? source) => Source = source;

    /// <summary>The bound data source.</summary>
    public object? Source { get; }

    /// <summary>Reads <paramref name="path"/> from the source, or <c>null</c>.</summary>
    /// <param name="path">A dotted member path.</param>
    public object? Resolve(string path) => MemberPath.Read(Source, path);

    /// <summary>Writes <paramref name="value"/> to <paramref name="path"/> on the source.</summary>
    /// <param name="path">A dotted member path.</param>
    /// <param name="value">The new value.</param>
    /// <returns><c>true</c> when the write succeeded (a settable member exists).</returns>
    public bool TrySet(string path, object? value) => MemberPath.TryWrite(Source, path, value);
}
