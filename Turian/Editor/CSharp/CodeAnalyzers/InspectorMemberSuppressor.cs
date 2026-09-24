namespace Turian.CSharp.CodeAnalyzers;

/// <summary>
/// Suppresses IDE0051 (private member never used)
/// and IDE0040 for members that are exposed to the Inspector.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public class InspectorMemberSuppressor : DiagnosticSuppressor
{
    // Define the rules we want to suppress
    // 1. Unused private members (for fields/props you might have made private but marked [Expose])
    static readonly SuppressionDescriptor unusedMemberRule = new(
        id: "SPR0001",
        suppressedDiagnosticId: "IDE0051",
        justification: "Member is accessed by the Engine Inspector via reflection.");

    // 2. The "Can be made private" suggestion (common for public properties)
    static readonly SuppressionDescriptor makePrivateRule = new(
        id: "SPR0002",
        suppressedDiagnosticId: "IDE0040", // Accessibility modifiers
        justification: "Member must remain public for Engine Inspector/Scripting access.");

    // 3. Field never assigned (the classic grayed out field)
    static readonly SuppressionDescriptor unassignedFieldRule = new(
        id: "SPR0003",
        suppressedDiagnosticId: "CS0649",
        justification: "Field is assigned via Engine serialization.");

    /// <inheritdoc/>
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions
        => [unusedMemberRule, makePrivateRule, unassignedFieldRule];

    /// <inheritdoc/>
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            // 1. Fix: Ensure Location and SourceTree are not null
            if (diagnostic.Location.SourceTree is not { } tree) continue;

            var root = tree.GetRoot(context.CancellationToken);
            var node = root.FindNode(diagnostic.Location.SourceSpan);

            var model = context.GetSemanticModel(tree);

            // 3. Fix: GetDeclaredSymbol can return null; handle it safely
            var symbol = model.GetDeclaredSymbol(node, context.CancellationToken);
            if (symbol == null) continue;

            if (symbol is IFieldSymbol field && IsEngineMember(field.ContainingType, field.DeclaredAccessibility, field.GetAttributes()))
            {
                SuppressMatching(context, diagnostic);
            }
            else if (symbol is IPropertySymbol prop && IsEngineMember(prop.ContainingType, prop.DeclaredAccessibility, prop.GetAttributes()))
            {
                SuppressMatching(context, diagnostic);
            }
        }
    }

    void SuppressMatching(SuppressionAnalysisContext context, Diagnostic diagnostic)
    {
        var descriptor = SupportedSuppressions.FirstOrDefault(s => s.SuppressedDiagnosticId == diagnostic.Id);
        if (descriptor != null)
        {
            context.ReportSuppression(Suppression.Create(descriptor, diagnostic));
        }
    }

    static bool IsEngineMember(INamedTypeSymbol containingType, Accessibility access, ImmutableArray<AttributeData> attributes)
    {
        // Traverse inheritance to see if it derives from Node or Component
        var current = containingType;
        var derivesFromEngine = false;
        while (current != null)
        {
            if (current.Name == "Node" || current.Name == "Component")
            {
                derivesFromEngine = true;
                break;
            }
            current = current.BaseType;
        }

        if (!derivesFromEngine) return false;

        // Logic: If it's public, it's for the Inspector.
        // Or if it's private/internal but has an [Expose] attribute.
        var isPublic = access == Accessibility.Public;
        var hasExposeAttribute = attributes.Any(a => a.AttributeClass?.Name == "ExposeAttribute" || a.AttributeClass?.Name == "SerializeField");

        return isPublic || hasExposeAttribute;
    }
}
