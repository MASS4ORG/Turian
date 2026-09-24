namespace Turian.Engine.UI;

/// <summary>
/// Code-behind for a <c>.ui</c> document. Subclass it, expose public properties for data bindings
/// and public parameterless methods for element events (<c>click="OnPlay"</c> → <c>OnPlay()</c>).
/// A <see cref="UiRenderer"/> discovers both automatically via <see cref="UiRenderer.Bind"/>.
/// </summary>
public abstract class UiController
{
    UiDocument? document;

    /// <summary>The document this controller is bound to.</summary>
    public UiDocument Document =>
        document ?? throw new InvalidOperationException("The controller is not bound to a document");

    /// <summary>Called once when the controller is bound. Wire up state here.</summary>
    protected virtual void OnBind() { }

    /// <summary>Called once per frame before the UI is built.</summary>
    /// <param name="deltaTime">Seconds since the previous frame.</param>
    public virtual void OnUpdate(float deltaTime) { }

    /// <summary>Called when the controller is detached.</summary>
    protected virtual void OnUnbind() { }

    /// <summary>Finds a parsed element by <c>name</c>.</summary>
    /// <param name="name">The element name.</param>
    public UiElement? Q(string name) => document?.Find(name);

    internal void Attach(UiDocument uiDocument)
    {
        document = uiDocument;
        OnBind();
    }

    internal void Detach()
    {
        OnUnbind();
        document = null;
    }

    /// <summary>Public parameterless <c>void</c> methods, keyed by name, as event actions.</summary>
    internal IReadOnlyDictionary<string, Action> DiscoverHandlers() =>
        GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Where(m => m is { IsSpecialName: false } && m.ReturnType == typeof(void) && m.GetParameters().Length == 0)
            .GroupBy(m => m.Name)
            .ToDictionary(g => g.Key, g => (Action)(() => g.First().Invoke(this, null)), StringComparer.Ordinal);
}
