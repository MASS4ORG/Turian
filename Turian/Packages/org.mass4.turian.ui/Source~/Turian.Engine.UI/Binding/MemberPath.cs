namespace Turian.Engine.UI;

/// <summary>Reads and writes a dotted <c>a.b.c</c> member path on an object graph by reflection.</summary>
public static class MemberPath
{
    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    /// <summary>Reads <paramref name="path"/> starting at <paramref name="root"/>, or <c>null</c> if any step is missing.</summary>
    /// <param name="root">The object the path is relative to.</param>
    /// <param name="path">A dotted member path; an empty path returns <paramref name="root"/>.</param>
    public static object? Read(object? root, string path)
    {
        if (string.IsNullOrEmpty(path)) return root;

        var current = root;
        foreach (var name in path.Split('.'))
        {
            if (current is null) return null;
            current = ReadMember(current, name);
        }

        return current;
    }

    /// <summary>Writes <paramref name="value"/> to the last segment of <paramref name="path"/>.</summary>
    /// <param name="root">The object the path is relative to.</param>
    /// <param name="path">A dotted member path with at least one segment.</param>
    /// <param name="value">The value to assign; converted to the target member type when possible.</param>
    /// <returns><c>true</c> when the assignment succeeded.</returns>
    public static bool TryWrite(object? root, string path, object? value)
    {
        if (root is null || string.IsNullOrEmpty(path)) return false;

        var segments = path.Split('.');
        var target = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            target = ReadMember(target, segments[i]);
            if (target is null) return false;
        }

        var last = segments[^1];

        if (target is IDictionary<string, object?> dict)
        {
            dict[last] = value;
            return true;
        }

        var type = target.GetType();

        if (type.GetProperty(last, flags) is { CanWrite: true } prop)
        {
            prop.SetValue(target, Coerce(value, prop.PropertyType));
            return true;
        }

        if (type.GetField(last, flags) is { } field)
        {
            field.SetValue(target, Coerce(value, field.FieldType));
            return true;
        }

        return false;
    }

    static object? ReadMember(object obj, string name)
    {
        switch (obj)
        {
            case IDictionary<string, object?> generic:
                return generic.TryGetValue(name, out var gv) ? gv : null;
            case IDictionary plain:
                return plain.Contains(name) ? plain[name] : null;
        }

        var type = obj.GetType();
        if (type.GetProperty(name, flags) is { } prop) return prop.GetValue(obj);
        if (type.GetField(name, flags) is { } field) return field.GetValue(obj);
        return null;
    }

    static object? Coerce(object? value, Type targetType)
    {
        if (value is null) return null;
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t.IsInstanceOfType(value)) return value;

        try
        {
            if (t.IsEnum && value is string es) return Enum.Parse(t, es, ignoreCase: true);
            return Convert.ChangeType(value, t, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is InvalidCastException or FormatException or OverflowException)
        {
            return value;
        }
    }
}
