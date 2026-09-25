namespace Turian.Editor.Core;

/// <summary>
/// Unified get/set over <see cref="FieldInfo"/> and <see cref="PropertyInfo"/>.
/// Mutation notification is optional — supply a callback when engine-side
/// dirty-marking is needed (Studio), omit it for CLI/headless use.
/// </summary>
public static class MemberValueAccessor
{
    /// <summary>
    /// Gets the value of a field or readable, non-indexed property.
    /// Returns <see langword="null"/> on failure instead of throwing.
    /// </summary>
    public static object? GetValue(this MemberInfo member, object source)
    {
        ArgumentNullException.ThrowIfNull(member);
        try
        {
            return member switch
            {
                FieldInfo f => f.GetValue(source),
                PropertyInfo p when p.GetIndexParameters().Length == 0 => p.GetValue(source),
                PropertyInfo => null,
                _ => throw new ArgumentException("Must be FieldInfo or PropertyInfo", nameof(member))
            };
        }
        catch (ArgumentException) { throw; }
        catch (Exception ex)
        {
            Log.Logger.LogDebug(ex, "Failed to get value for {Member}", member.Name);
            return null;
        }
    }

    /// <summary>
    /// Sets the value of a field or writable property.
    /// Invokes <paramref name="onMutated"/> with the target after writing successfully,
    /// so the caller can handle dirty-marking / node refresh.
    /// </summary>
    public static void SetValue(
        this MemberInfo member,
        object target,
        object? value,
        Action<object>? onMutated = null)
    {
        switch (member)
        {
            case FieldInfo f:
                f.SetValue(target, value);
                onMutated?.Invoke(target);
                break;
            case PropertyInfo { CanWrite: true } p:
                try { p.SetValue(target, value); onMutated?.Invoke(target); }
                catch { /* setter threw — silently ignore */ }
                break;
            case PropertyInfo:
                break;
            default:
                throw new ArgumentException("Must be FieldInfo or PropertyInfo", nameof(member));
        }
    }

    /// <summary>
    /// Sets the value only when it differs from the current one.
    /// Returns <see langword="true"/> if the value was actually changed.
    /// </summary>
    public static bool TrySetValue(
        MemberInfo member,
        object target,
        object? newValue,
        Action<object>? onMutated = null)
    {
        ArgumentNullException.ThrowIfNull(member);
        ArgumentNullException.ThrowIfNull(target);

        // Null is a real value only for a reference to a node, component or DataAsset: it clears the slot.
        var memberType = member is PropertyInfo property ? property.PropertyType : ((FieldInfo)member).FieldType;
        if (newValue is null && !ObjectReferences.IsReferenceMember(memberType, allowSceneObjects: true)) return false;

        object? current;
        try { current = member.GetValue(target); }
        catch { return false; }

        if (Equals(current, newValue)) return false;

        member.SetValue(target, newValue, onMutated);
        return true;
    }
}
