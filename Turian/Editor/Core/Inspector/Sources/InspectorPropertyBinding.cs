namespace Turian.Editor.Core;

/// <summary>
/// An <see cref="InspectorProperty"/> of one selected object, as the target of its form field, so a field drawer can
/// read the editor hint and attributes. A brick's failures stop here.
/// </summary>
public sealed class InspectorPropertyBinding
{
    readonly InspectorSourceCatalog catalog;
    bool readFailed;

    internal InspectorPropertyBinding(InspectorProperty property, object subject, InspectorSourceCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(property.ValueType);
        Property = property;
        Subject = subject;
        this.catalog = catalog;
    }

    /// <summary>What the source described.</summary>
    public InspectorProperty Property { get; }

    /// <summary>The selected object the value belongs to.</summary>
    public object Subject { get; }

    /// <summary>
    /// Whether the object carries a value of its own: what the source says, else whether the value differs from the
    /// property's default.
    /// </summary>
    public bool IsSet => Property.IsSet is { } isSet
        ? catalog.Query(Property.Label, isSet)
        : Property.Default is { } value && catalog.Query(Property.Label, () => !Matches(Read(), value));

    /// <summary>Whether the object's own value can be removed, or the default written over it.</summary>
    public bool CanReset => !Property.IsReadOnly && (Property.Reset is not null || Property.Default is not null);

    /// <summary>Removes the object's own value, writing the default when the source has no way of its own.</summary>
    public void Reset()
    {
        if (!CanReset)
        {
            return;
        }

        if (Property.Reset is { } reset)
        {
            catalog.Run(Property.Label, reset);
        }
        else
        {
            Write(Property.Default);
        }
    }

    // A default typed into a source rarely has the exact bits of a value that went through an editor.
    static bool Matches(object? value, object other) => (value, other) switch
    {
        (float a, float b) => Math.Abs(a - b) <= 1e-6f * Math.Max(1f, Math.Abs(b)),
        (double a, double b) => Math.Abs(a - b) <= 1e-12 * Math.Max(1d, Math.Abs(b)),
        _ => Equals(value, other),
    };

    /// <summary>The form field over the property.</summary>
    /// <param name="label">The label, shared by every selected object's field.</param>
    internal FormField Field(string label) =>
        NewField(label, Property.ValueType, this, Read, Write, null, Property.IsReadOnly);

    // A drawer casts what it reads to the declared type, so a failed or mistyped read shows that type's blank.
    object? Read()
    {
        try
        {
            var value = Property.Get();
            var type = Property.ValueType;
            var fits = value is null
                ? !type.IsValueType || Nullable.GetUnderlyingType(type) is not null
                : type.IsInstanceOfType(value);
            return fits ? value : throw new InvalidCastException($"The value is not a {type.Name}.");
        }
        catch (Exception ex)
        {
            if (!readFailed)
            {
                readFailed = true;
                Log.Logger.LogError(ex, "The Inspector could not read {Property}", Property.Id);
            }

            return Blank(Property.ValueType);
        }
    }

    static object? Blank(Type type) => type.IsValueType && Nullable.GetUnderlyingType(type) is null
        ? RuntimeHelpers.GetUninitializedObject(type)
        : null;

    bool Write(object? value)
    {
        if (Equals(Read(), value))
        {
            return false;
        }

        return catalog.Run(Property.Label, () => Property.Set(value));
    }

    // Autoformers builds fields over reflected members publicly; a field over two accessors is only reachable this way.
    [UnsafeAccessor(UnsafeAccessorKind.Constructor)]
    static extern FormField NewField(string name, Type valueType, object target, Func<object?> read,
        Func<object?, bool> write, Action<object>? mutationNotifier, bool isReadOnly);
}
