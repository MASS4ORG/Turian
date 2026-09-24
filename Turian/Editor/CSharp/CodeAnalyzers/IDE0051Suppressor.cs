namespace Turian.CSharp.CodeAnalyzers;

/// <summary>
/// Suppresses the IDE0051 (private method never user) for methods with MenuItemAttribute.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
// ReSharper disable once InconsistentNaming
public class IDE0051Suppressor : DiagnosticSuppressor
{
    /// <summary>
    /// Description of the rule to suppress.
    /// </summary>
    static readonly SuppressionDescriptor suppressionRule =
        new(
            "SPR0001",
            "IDE0051", // This is the ID of the diagnostic you want to suppress
            "Methods with MenuItemAttribute should not trigger IDE0051."
        );

    /// <inheritdoc/>
    public override ImmutableArray<SuppressionDescriptor> SupportedSuppressions { get; } = [suppressionRule];

    /// <inheritdoc/>
    public override void ReportSuppressions(SuppressionAnalysisContext context)
    {
        foreach (var diagnostic in context.ReportedDiagnostics)
        {
            var sourceTree = diagnostic.Location.SourceTree;
            if (sourceTree == null)
            {
                continue;
            }

            var root = sourceTree.GetRoot(context.CancellationToken);
            var node = root.FindNode(diagnostic.Location.SourceSpan);
            var methodDeclaration =
                node.FirstAncestorOrSelf<Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax>();

            if (methodDeclaration == null)
            {
                continue;
            }

            foreach (var attributeList in methodDeclaration.AttributeLists)
            {
                foreach (var attribute in attributeList.Attributes)
                {
                    var attributeSymbol = context
                        .GetSemanticModel(sourceTree)
                        .GetTypeInfo(attribute, context.CancellationToken)
                        .Type;

                    if (
                        attributeSymbol != null
                        && HasSuppressPrivateAttribute(context, attributeSymbol)
                    )
                    {
                        context.ReportSuppression(Suppression.Create(suppressionRule, diagnostic));
                    }
                }
            }
        }
    }

    static bool HasSuppressPrivateAttribute(
        SuppressionAnalysisContext context,
        ITypeSymbol attributeSymbol
    )
    {
        var suppressPrivateAttributeSymbol = context.Compilation.GetTypeByMetadataName(
            "Turian.SuppressPrivateAttribute"
        );
        if (suppressPrivateAttributeSymbol == null)
        {
            return false;
        }

        return attributeSymbol
            .GetAttributes()
            .Any(attr =>
                SymbolEqualityComparer.Default.Equals(
                    attr.AttributeClass,
                    suppressPrivateAttributeSymbol
                )
            );
    }
}
