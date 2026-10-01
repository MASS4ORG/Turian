namespace Turian.Editor.Core;

/// <summary>
/// Turns an object into a <see cref="FormModel"/> by reflection. The member list is cached per type,
/// because an inspector rebuilds this many times a second.
/// </summary>
public static class FormBuilder
{
    const BindingFlags memberScope =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    static readonly ConditionalWeakTable<Type, Lazy<IReadOnlyList<InspectorMemberMetadata>>> MembersByType = new();
    static readonly ConditionalWeakTable<Type, Lazy<IReadOnlyList<MemberInfo>>> MemberViewsByType = new();
    static readonly ConditionalWeakTable<Type, Lazy<MethodInfo[]>> ButtonMethodsByType = new();
    static readonly InspectorMemberMetadata ActiveSwitch =
        InspectorMemberMetadata.For(typeof(Component).GetProperty(nameof(Component.IsActive))!);

    /// <summary>Builds the form for a plain object: one section holding its editable members.</summary>
    /// <param name="target">The object to inspect.</param>
    /// <param name="mutationNotifier">Called with the target after any field is written.</param>
    /// <param name="readOnly">Disables edits throughout the object's fields.</param>
    public static FormModel Build(object target, Action<object>? mutationNotifier = null, bool readOnly = false)
    {
        ArgumentNullException.ThrowIfNull(target);

        var title = target is IInspectorTitled titled ? titled.InspectorTitle : target.GetType().Name;
        return new FormModel(target, [SectionFor(target, title, mutationNotifier, readOnly: readOnly)]);
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

    /// <summary>The editable members of a type, in inspector order.</summary>
    /// <param name="type">The type to reflect over.</param>
    public static IReadOnlyList<MemberInfo> EditableMembers(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return MemberViewsByType.GetValue(type, static t =>
            new Lazy<IReadOnlyList<MemberInfo>>(() =>
                Array.AsReadOnly([.. EditableMetadata(t).Select(metadata => metadata.Member)]))).Value;
    }

    /// <summary>Cached, ordered metadata for the visible members of a type.</summary>
    public static IReadOnlyList<InspectorMemberMetadata> EditableMetadata(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return MembersByType.GetValue(type, static t =>
            new Lazy<IReadOnlyList<InspectorMemberMetadata>>(() =>
                Array.AsReadOnly([
                    .. t.GetMembers(memberScope)
                        .Where(member => member is FieldInfo { FieldType.IsByRefLike: false } || member is PropertyInfo
                            {
                                GetMethod: not null
                            } property
                            && property.GetIndexParameters().Length == 0
                            && !property.PropertyType.IsByRefLike)
                        .Select(InspectorMemberMetadata.For)
                        .Where(metadata => metadata.IsVisible)
                        .OrderBy(metadata => metadata.Priority)
                ]))).Value;
    }

    static FormSection SectionFor(
        object target, string title, Action<object>? mutationNotifier, bool removable = false, bool readOnly = false)
    {
        var fields = EditableMetadata(target.GetType())
            .Where(metadata => CanReadSafely(metadata, target))
            .Select(metadata => new FormField(metadata.Member, target, mutationNotifier, readOnly))
            .ToList();

        if (target is Component && fields.TrueForAll(f => f.Name != ActiveSwitch.Member.Name))
            fields.Insert(0, new FormField(ActiveSwitch.Member, target, mutationNotifier, readOnly));

        var buttons = ButtonMethodsByType.GetValue(target.GetType(), static t =>
            new Lazy<MethodInfo[]>(() => [.. t
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(method => method.GetCustomAttribute<ButtonAttribute>() is not null)
                .Where(method => method.GetParameters().Length == 0)])).Value
            .Where(_ => !readOnly)
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

    /// <summary>A property getter can throw on a half-built object; such members are skipped.</summary>
    static bool CanReadSafely(InspectorMemberMetadata metadata, object target)
    {
        if (metadata.Member is not PropertyInfo) return true;

        try
        {
            _ = metadata.GetValue(target);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
