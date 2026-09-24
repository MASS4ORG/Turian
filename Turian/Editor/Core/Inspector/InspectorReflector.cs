// Turian.Editor.Core/Inspector/InspectorReflector.cs

namespace Turian.Editor.Core;

/// <summary>
/// Pure-reflection helpers for discovering which members of an object
/// should be surfaced in an inspector panel.
/// </summary>
public static class InspectorReflector
{
    /// <summary>
    /// Returns all members of <paramref name="targetObject"/> that should be
    /// displayed in an inspector, filtering out unsafe/hidden members.
    /// </summary>
    public static IEnumerable<MemberInfo> GetDisplayableMembers(object targetObject)
    {
        ArgumentNullException.ThrowIfNull(targetObject);
        return targetObject
            .GetType()
            .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.MemberType is MemberTypes.Field or MemberTypes.Property)
            .Where(m => ShouldDisplay(m) && CanSafelyAccess(m, targetObject));
    }

    /// <summary>
    /// Returns <see langword="true"/> if the member should be shown
    /// based on visibility and editor attributes.
    /// </summary>
    static bool ShouldDisplay(MemberInfo member)
    {
        var isPublic = member switch
        {
            PropertyInfo p => (p.GetMethod?.IsPublic ?? false) && p is { CanWrite: true, CanRead: true },
            FieldInfo f => f.IsPublic,
            _ => false
        };

        return (isPublic && member.GetCustomAttribute<HideInEditorAttribute>() is null)
               || member.GetCustomAttribute<ShowInEditorAttribute>() is not null;
    }

    /// <summary>
    /// Returns <see langword="true"/> if accessing the member value will not throw.
    /// Fields are always considered safe; property getters are probed.
    /// </summary>
    static bool CanSafelyAccess(MemberInfo member, object targetObject)
    {
        if (member is FieldInfo) return true;
        if (member is not PropertyInfo prop) return true;

        try
        {
            _ = prop.GetValue(targetObject);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
