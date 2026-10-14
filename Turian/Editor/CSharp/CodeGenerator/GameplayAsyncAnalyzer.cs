namespace Turian.CSharp.CodeGenerator;

/// <summary>Flags gameplay async patterns that bypass lifetime handling or violate pooled-value consumption.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GameplayAsyncAnalyzer : DiagnosticAnalyzer
{
    static readonly DiagnosticDescriptor AsyncVoid = new("TUR0002", "Observe gameplay async routines",
        "Component async code must return Task or ValueTask instead of async void", "Gameplay",
        DiagnosticSeverity.Error, true);
    static readonly DiagnosticDescriptor Delay = new("TUR0003", "Use the game clock for gameplay waits",
        "Task.Delay bypasses gameplay time and lifetime; use the component's Async waits", "Gameplay",
        DiagnosticSeverity.Warning, true);
    static readonly DiagnosticDescriptor StoredValue = new("TUR0004", "Preserve shared async results",
        "Field '{0}' stores a single-consumption ValueTask; store a Task for shared results", "Gameplay",
        DiagnosticSeverity.Warning, true);

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [AsyncVoid, Delay, StoredValue];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSymbolAction(AnalyzeMethod, SymbolKind.Method);
        context.RegisterSymbolAction(AnalyzeField, SymbolKind.Field);
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeAnonymousFunction, OperationKind.AnonymousFunction);
        context.RegisterOperationAction(AnalyzeLocalFunction, OperationKind.LocalFunction);
    }

    static void AnalyzeMethod(SymbolAnalysisContext context)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (UnsafeMethod(method))
        {
            context.ReportDiagnostic(Diagnostic.Create(AsyncVoid, method.Locations.FirstOrDefault()));
        }
    }

    static void AnalyzeField(SymbolAnalysisContext context)
    {
        var field = (IFieldSymbol)context.Symbol;
        if (IsComponent(field.ContainingType) && field.Type.Name == "ValueTask" &&
            field.Type.ContainingNamespace?.ToDisplayString() == "System.Threading.Tasks")
        {
            context.ReportDiagnostic(Diagnostic.Create(StoredValue, field.Locations.FirstOrDefault(), field.Name));
        }
    }

    static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (Microsoft.CodeAnalysis.Operations.IInvocationOperation)context.Operation;
        if (invocation.TargetMethod.Name == "Delay" &&
            invocation.TargetMethod.ContainingType.ToDisplayString() == "System.Threading.Tasks.Task" &&
            IsComponent(context.ContainingSymbol.ContainingType))
        {
            context.ReportDiagnostic(Diagnostic.Create(Delay, invocation.Syntax.GetLocation()));
        }
    }

    static void AnalyzeAnonymousFunction(OperationAnalysisContext context)
    {
        var function = (Microsoft.CodeAnalysis.Operations.IAnonymousFunctionOperation)context.Operation;
        if (UnsafeMethod(function.Symbol))
        {
            context.ReportDiagnostic(Diagnostic.Create(AsyncVoid, function.Syntax.GetLocation()));
        }
    }

    static void AnalyzeLocalFunction(OperationAnalysisContext context)
    {
        var function = (Microsoft.CodeAnalysis.Operations.ILocalFunctionOperation)context.Operation;
        if (UnsafeMethod(function.Symbol))
        {
            context.ReportDiagnostic(Diagnostic.Create(AsyncVoid, function.Syntax.GetLocation()));
        }
    }

    static bool UnsafeMethod(IMethodSymbol method)
        => method.IsAsync && method.ReturnsVoid && IsComponent(method.ContainingType);

    static bool IsComponent(INamedTypeSymbol? type)
    {
        for (; type is not null; type = type.BaseType)
        {
            if (type.ToDisplayString() == "Turian.Engine.Core.Component")
            {
                return true;
            }
        }

        return false;
    }
}
