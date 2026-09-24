namespace Turian.Editor.Core;

/// <summary>
/// Turns an object into a <see cref="FormModel"/> by reflection. The member list is cached per type,
/// because an inspector rebuilds this many times a second.
/// </summary>
public static class FormBuilder
{
    const BindingFlags memberScope =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static readonly ConcurrentDictionary<Type, MemberInfo[]> membersByType = new();
    static readonly ConcurrentDictionary<Type, MethodInfo[]> buttonMethodsByType = new();

    /// <summary>Builds the form for a plain object: one section holding its editable members.</summary>
    /// <param name="target">The object to inspect.</param>
    /// <param name="mutationNotifier">Called with the target after any field is written.</param>
    public static FormModel Build(object target, Action<object>? mutationNotifier = null)
    {
        ArgumentNullException.ThrowIfNull(target);

        return new FormModel(target, [SectionFor(target, target.GetType().Name, mutationNotifier)]);
    }

    /// <summary>
    /// Builds the form for a node: its own members first, then one removable section per component,
    /// which is the shape an inspector shows.
    /// </summary>
    /// <param name="node">The node to inspect.</param>
    /// <param name="mutationNotifier">Called with the mutated object after any field is written.</param>
    public static FormModel BuildForNode(Node node, Action<object>? mutationNotifier = null)
    {
        ArgumentNullException.ThrowIfNull(node);

        var sections = new List<FormSection> { SectionFor(node, node.Name, mutationNotifier) };
        sections.AddRange(node.Components.Select(component =>
            SectionFor(component, component.GetType().Name, mutationNotifier, removable: true)));

        return new FormModel(node, sections);
    }

    /// <summary>The editable members of a type, in declaration order.</summary>
    /// <param name="type">The type to reflect over.</param>
    public static IReadOnlyList<MemberInfo> EditableMembers(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return membersByType.GetOrAdd(type, static t => [.. t
            .GetMembers(memberScope)
            .Where(member => member.MemberType is MemberTypes.Field or MemberTypes.Property)
            .Where(ShouldDisplay)]);
    }

    static FormSection SectionFor(
        object target, string title, Action<object>? mutationNotifier, bool removable = false)
    {
        var fields = EditableMembers(target.GetType())
            .Where(member => CanReadSafely(member, target))
            .Select(member => new FormField(member, target, mutationNotifier))
            .ToList();

        if (ActiveSwitch(target) is { } active && fields.TrueForAll(f => f.Name != active.Name))
            fields.Insert(0, new FormField(active, target, mutationNotifier));

        var buttons = buttonMethodsByType.GetOrAdd(target.GetType(), static t => [.. t
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(method => method.GetCustomAttribute<ButtonAttribute>() is not null)
            .Where(method => method.GetParameters().Length == 0)])
            .Select(method => new InspectorButton(FormField.Humanize(method.Name),
                () =>
                {
                    try
                    {
                        method.Invoke(target, null);
                        mutationNotifier?.Invoke(target);
                    }
                    catch (TargetInvocationException ex) when (ex.InnerException is not null)
                    {
                        Log.Logger.LogError(ex.InnerException, "Inspector button {Method} failed on {Target}",
                            method.Name, target.GetType().Name);
                    }
                }))
            .ToList();

        return new FormSection(title, target, fields, removable) { Buttons = buttons };
    }

    /// <summary>
    /// A component's <c>IsActive</c>, which is <c>[HideInEditor]</c> because the section heading draws
    /// it rather than the body. Null for anything that is not a component.
    /// </summary>
    static MemberInfo? ActiveSwitch(object target) =>
        target is Component ? typeof(Component).GetProperty(nameof(Component.IsActive)) : null;

    /// <summary>
    /// Public and writable, unless hidden; or explicitly shown. Matches the Avalonia inspector so both
    /// shells display the same members.
    /// </summary>
    static bool ShouldDisplay(MemberInfo member)
    {
        var isPublic = member switch
        {
            PropertyInfo property => (property.GetMethod?.IsPublic ?? false) && property.CanRead && property.CanWrite,
            FieldInfo field => field.IsPublic,
            _ => false
        };

        return (isPublic && member.GetCustomAttribute<HideInEditorAttribute>() is null)
               || member.GetCustomAttribute<ShowInEditorAttribute>() is not null;
    }

    /// <summary>A property getter can throw on a half-built object; such members are skipped.</summary>
    static bool CanReadSafely(MemberInfo member, object target)
    {
        if (member is not PropertyInfo property) return true;

        try
        {
            _ = property.GetValue(target);
            return true;
        }
        catch (Exception ex) when (ex is TargetInvocationException or InvalidOperationException or NotSupportedException)
        {
            return false;
        }
    }
}
