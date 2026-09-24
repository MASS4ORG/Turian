namespace Turian.Editor.Core;

/// <summary>
/// One editable value of an object, described without reference to any UI toolkit: what it is called,
/// what type it holds, and how to read and write it. Usually a reflected member, but a collection's
/// element is the same thing seen through an index or a key.
/// </summary>
public sealed class FormField
{
    readonly MemberInfo? member;
    readonly Func<object?> read;
    readonly Func<object?, bool> write;
    readonly Action<object>? mutationNotifier;
    readonly bool forcedReadOnly;

    internal FormField(MemberInfo member, object target, Action<object>? mutationNotifier)
    {
        this.member = member;
        this.mutationNotifier = mutationNotifier;

        Target = target;
        Name = member.Name;
        Label = Humanize(member.Name);
        ValueType = member switch
        {
            PropertyInfo property => property.PropertyType,
            FieldInfo field => field.FieldType,
            _ => typeof(object)
        };

        read = () => member.GetValue(target);
        write = value => MemberValueAccessor.TrySetValue(member, target, value, mutationNotifier);
    }

    /// <summary>
    /// Creates a field over an accessor pair rather than a member, which is how a list entry or a
    /// dictionary value is edited: it belongs to no <see cref="MemberInfo"/>, but reads and writes
    /// exactly like one.
    /// </summary>
    internal FormField(string name, Type valueType, object target, Func<object?> read,
        Func<object?, bool> write, Action<object>? mutationNotifier, bool isReadOnly)
    {
        this.read = read;
        this.write = write;
        this.mutationNotifier = mutationNotifier;
        forcedReadOnly = isReadOnly;

        Target = target;
        Name = name;
        Label = name;
        ValueType = valueType;
    }

    /// <summary>The object this value belongs to.</summary>
    public object Target { get; }

    /// <summary>The member's name, or the element's index or key.</summary>
    public string Name { get; }

    /// <summary>A display label derived from the name.</summary>
    public string Label { get; }

    /// <summary>The type the value holds, which selects the drawer.</summary>
    public Type ValueType { get; }

    /// <summary>Reads the current value.</summary>
    public object? GetValue() => read();

    /// <summary>
    /// Writes a value, notifying the editor that the target changed so dirty state and any live views
    /// follow.
    /// </summary>
    /// <param name="value">The value to write.</param>
    /// <returns>True if the write succeeded.</returns>
    public bool SetValue(object? value) => !IsReadOnly && write(value);

    /// <summary>
    /// Reports that the value behind this field was changed in place, for types edited through their
    /// own properties rather than by assignment — a transform, say.
    /// </summary>
    public void Touch() => mutationNotifier?.Invoke(Target);

    /// <summary>Whether the value refuses writes, from <c>[ReadOnly]</c> or a missing setter.</summary>
    public bool IsReadOnly =>
        forcedReadOnly
        || member?.GetCustomAttribute<ReadOnlyAttribute>() is not null
        || (member is PropertyInfo property && !property.CanWrite);

    /// <summary>The inclusive bounds from <c>[Range]</c>, or null when the value is unbounded.</summary>
    public (float Min, float Max)? Range =>
        Attribute<RangeAttribute>() is { } range ? (range.Min, range.Max) : null;

    /// <summary>Looks up an attribute on the member, for drawers that honour ranges, tooltips and such.</summary>
    /// <typeparam name="TAttribute">The attribute to find.</typeparam>
    public TAttribute? Attribute<TAttribute>() where TAttribute : Attribute =>
        member?.GetCustomAttribute<TAttribute>();

    /// <summary>Turns <c>MaxResolution</c> into <c>Max Resolution</c>.</summary>
    internal static string Humanize(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var text = new StringBuilder(name.Length + 8);
        text.Append(char.ToUpperInvariant(name[0]));

        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && !char.IsUpper(name[i - 1])) text.Append(' ');
            text.Append(name[i]);
        }

        return text.ToString();
    }
}
