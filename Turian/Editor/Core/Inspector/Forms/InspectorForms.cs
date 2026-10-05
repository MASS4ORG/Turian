namespace Turian.Editor.Core;

/// <summary>
/// Turian's rules over the generic <see cref="FormBuilder"/>: null clears only node, component and DataAsset
/// references, caught failures go to <see cref="Log"/>, components head their section with their active switch,
/// and a node's form adds one removable section per component.
/// </summary>
public static class InspectorForms
{
    static readonly PropertyInfo ActiveSwitch = typeof(Component).GetProperty(nameof(Component.IsActive))!;

    /// <summary>Form options carrying Turian's null and failure rules.</summary>
    /// <param name="mutationNotifier">Called with the mutated object after any field is written.</param>
    /// <param name="readOnly">Disables edits and actions throughout the form.</param>
    public static FormOptions Options(Action<object>? mutationNotifier = null, bool readOnly = false) => new()
    {
        MutationNotifier = mutationNotifier,
        ReadOnly = readOnly,
        AcceptsNull = static type => ObjectReferences.IsReferenceMember(type, allowSceneObjects: true),
        FailureReporter = Report,
    };

    /// <summary>Builds the form for a plain object, a component or a DataAsset: one section of its members.</summary>
    /// <param name="target">The object to inspect.</param>
    /// <param name="mutationNotifier">Called with the target after any field is written.</param>
    /// <param name="readOnly">Disables edits throughout the object's fields.</param>
    public static FormModel Build(object target, Action<object>? mutationNotifier = null, bool readOnly = false)
    {
        var options = Options(mutationNotifier, readOnly);
        var model = FormBuilder.Build(target, options);
        return model with { Sections = [.. model.Sections.Select(section => WithActiveSwitch(section, options))] };
    }

    /// <summary>Builds the fields common to objects of the same type, preserving each object's mutation callback.</summary>
    public static FormModel BuildForObjects(IReadOnlyList<object> targets, Action<object>? mutationNotifier = null)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0) return FormModel.Empty;
        if (targets.Any(target => target.GetType() != targets[0].GetType())) return FormModel.Empty;
        if (targets.Count == 1) return Build(targets[0], mutationNotifier);
        var sections = targets.Select(target => Build(target, mutationNotifier).Sections[0]).ToArray();
        return new FormModel(targets[0], [CombineSections(sections, sections[0].Title)]);
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

        var options = Options(mutationNotifier);
        var sections = new List<FormSection> { WithActiveSwitch(FormBuilder.Section(node, node.Name, options), options) };
        sections.AddRange(node.Components.Select(component => WithActiveSwitch(
            FormBuilder.Section(component, component.GetType().Name, options, removable: true), options)));

        return new FormModel(node, sections);
    }

    /// <summary>Builds shared node fields and components matched by type and occurrence across every selected node.</summary>
    public static FormModel BuildForNodes(IReadOnlyList<Node> nodes, Action<object>? mutationNotifier = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (nodes.Count == 0) return FormModel.Empty;
        if (nodes.Count == 1) return BuildForNode(nodes[0], mutationNotifier);

        var models = nodes.Select(node => BuildForNode(node, mutationNotifier)).ToArray();
        var sections = new List<FormSection>
        {
            CombineSections(models.Select(model => model.Sections[0]).ToArray(), $"{nodes.Count} Objects"),
        };
        var occurrences = new Dictionary<Type, int>();
        foreach (var section in models[0].Sections.Skip(1))
        {
            var type = section.Target.GetType();
            var occurrence = occurrences.GetValueOrDefault(type);
            occurrences[type] = occurrence + 1;
            var common = models.Select(model => model.Sections.Skip(1)
                .Where(candidate => candidate.Target.GetType() == type).ElementAtOrDefault(occurrence)).ToArray();
            if (common.All(candidate => candidate is not null))
                sections.Add(CombineSections(common.OfType<FormSection>().ToArray(), section.Title));
        }
        return new FormModel(nodes[0], sections);
    }

    static FormSection CombineSections(IReadOnlyList<FormSection> sections, string title)
    {
        var first = sections[0];
        var fields = new List<FormField>();
        foreach (var field in first.Fields)
        {
            var common = sections.Select(section => section.Fields.FirstOrDefault(candidate =>
                candidate.Name == field.Name && candidate.ValueType == field.ValueType)).ToArray();
            if (common.All(candidate => candidate is not null))
                fields.Add(FormField.Combine(common.OfType<FormField>()));
        }
        var enabled = fields.FirstOrDefault(field => field.Name == first.EnabledField?.Name);
        return first with { Title = title, Fields = fields, EnabledField = enabled, Buttons = [] };
    }

    /// <summary>
    /// Moves the target's <c>IsActive</c> switch into the heading. A component always gets one, although the
    /// property itself is hidden from its member list.
    /// </summary>
    static FormSection WithActiveSwitch(FormSection section, FormOptions options)
    {
        var fields = section.Fields;
        if (section.Target is Component && fields.All(field => field.Name != ActiveSwitch.Name))
            fields = [FormField.ForMember(ActiveSwitch, section.Target, options), .. fields];

        var enabled = fields.FirstOrDefault(field => field.Name == ActiveSwitch.Name && field.ValueType == typeof(bool));
        return section with { Fields = fields, EnabledField = enabled };
    }

    static void Report(FormFailure failure)
    {
        if (failure.Kind == FormFailureKind.Action)
        {
            Log.Logger.LogError(failure.Exception, "Inspector button {Method} failed on {Target}",
                failure.Member, failure.Target.GetType().Name);
            return;
        }

        Log.Logger.LogDebug(failure.Exception, "Inspector {Kind} of {Member} failed on {Target}",
            failure.Kind, failure.Member, failure.Target.GetType().Name);
    }
}
