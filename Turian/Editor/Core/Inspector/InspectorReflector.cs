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
        return FormBuilder.EditableMetadata(targetObject.GetType())
            .Where(metadata => CanSafelyAccess(metadata, targetObject))
            .Select(metadata => metadata.Member);
    }

    /// <summary>
    /// Returns <see langword="true"/> if accessing the member value will not throw.
    /// Fields are always considered safe; property getters are probed.
    /// </summary>
    static bool CanSafelyAccess(MemberMetadata metadata, object targetObject)
    {
        if (metadata.Member is not PropertyInfo) return true;

        try
        {
            _ = metadata.GetValue(targetObject);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
