using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Turian.CSharp.CodeGenerator;

/// <summary>
/// Reports a serialized DataAsset member typed as a node or component. A DataAsset outlives any scene, so it cannot
/// reference scene objects: the member would be saved as an inline copy. <c>NodeRef&lt;T&gt;</c> or
/// <c>ComponentRef&lt;T&gt;</c> hold an id resolved against a scene at runtime instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DataAssetSceneReferenceAnalyzer : DiagnosticAnalyzer
{
    /// <summary>The diagnostic id.</summary>
    public const string DiagnosticId = "TUR0001";

    static readonly DiagnosticDescriptor rule = new(
        DiagnosticId,
        "DataAssets cannot reference scene objects",
        "DataAsset member '{0}' holds a {1}; use {2}<{3}> to reference a scene object by id",
        "Turian.Serialization",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A DataAsset outlives any scene, so a node or component member is saved as an inline copy "
                     + "instead of a reference. NodeRef<T> and ComponentRef<T> store an id resolved at runtime.");

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [rule];

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(Analyze, SymbolKind.Field, SymbolKind.Property);
    }

    static void Analyze(SymbolAnalysisContext context)
    {
        var member = context.Symbol;
        if (member.IsStatic || member.DeclaredAccessibility != Accessibility.Public) return;
        if (!DerivesFrom(member.ContainingType, "Turian.Engine.Core.DataAsset")) return;
        if (member.GetAttributes().Any(static a => a.AttributeClass?.ToDisplayString() is
                "System.Text.Json.Serialization.JsonIgnoreAttribute" or "Turian.SerializeInlineAttribute"))
            return;

        var type = member switch
        {
            IFieldSymbol { IsConst: false, IsImplicitlyDeclared: false } field => field.Type,
            IPropertySymbol { IsIndexer: false, SetMethod: not null } property => property.Type,
            _ => null,
        };
        if (type is null) return;

        var element = type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named => named.TypeArguments[0],
            _ => type,
        };

        var kind = DerivesFrom(element, "Turian.Engine.Core.Component") ? "component"
            : DerivesFrom(element, "Turian.Engine.Core.Node") ? "node"
            : null;
        if (kind is null) return;

        context.ReportDiagnostic(Diagnostic.Create(rule, member.Locations.FirstOrDefault(), member.Name, kind,
            kind == "component" ? "ComponentRef" : "NodeRef", element.Name));
    }

    static bool DerivesFrom(ITypeSymbol? type, string baseName)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseName) return true;
        }

        return false;
    }
}
