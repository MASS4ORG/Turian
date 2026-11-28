namespace Turian.Editor.Core;

/// <summary>
/// Turns the schemas of the selected subjects into a form: a heading section, then one section per category. Several
/// subjects share the properties whose id and type match, each edit reaching every one of them.
/// </summary>
static class InspectorSchemaForms
{
    /// <summary>Builds the form for subjects and their schemas, given in the same order.</summary>
    public static FormModel Build(IReadOnlyList<object> subjects, IReadOnlyList<InspectorSchema> schemas,
        InspectorSourceCatalog catalog)
    {
        var fields = schemas[0].Properties.DistinctBy(static property => property.Id)
            .Select(property => (property.Category, Field: Shared(property, subjects, schemas, catalog)))
            .Where(static entry => entry.Field is not null)
            .ToLookup(static entry => entry.Category ?? "", static entry => entry.Field!);

        var heading = subjects.Count == 1 ? schemas[0].Title : $"{subjects.Count} Objects";
        var sections = new List<FormSection>
        {
            new(heading, subjects[0], [.. fields[""]])
            {
                Buttons = subjects.Count == 1 ? Actions(schemas[0], catalog) : [],
            },
        };
        sections.AddRange(fields.Where(static category => category.Key.Length > 0)
            .Select(category => new FormSection(category.Key, subjects[0], [.. category])));

        return new FormModel(subjects[0], sections);
    }

    // Null when a selected subject lacks the property.
    static FormField? Shared(InspectorProperty property, IReadOnlyList<object> subjects,
        IReadOnlyList<InspectorSchema> schemas, InspectorSourceCatalog catalog)
    {
        var own = new List<FormField>();
        for (var i = 0; i < subjects.Count; i++)
        {
            var match = i == 0
                ? property
                : schemas[i].Properties.FirstOrDefault(candidate =>
                    candidate.Id == property.Id && candidate.ValueType == property.ValueType);
            if (match is null)
            {
                return null;
            }

            own.Add(new InspectorPropertyBinding(match, subjects[i], catalog).Field(property.Label));
        }

        return FormField.Combine(own);
    }

    static IReadOnlyList<Button> Actions(InspectorSchema schema, InspectorSourceCatalog catalog) =>
    [
        .. (schema.Actions ?? []).Select(action => new Button(action.Label,
            () => catalog.Run(action.Label, action.Invoke),
            () => catalog.Query(action.Label, () => action.IsEnabled))),
    ];
}
