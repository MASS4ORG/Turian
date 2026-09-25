using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Turian.CSharp.CodeGenerator;

/// <summary>
/// Generates, for every DataAsset type, reflection-free member access registered with
/// <c>GeneratedSerializers</c>, and the implementation of each <c>[Observable]</c> partial property.
/// </summary>
/// <remarks>
/// A type is skipped, and keeps the reflection path, when generated code could not reproduce what reflection does:
/// members it cannot reach (non-public setters, <c>[JsonInclude]</c> non-public members, readonly fields), Asset-typed
/// members, duplicate names, or a type that is generic, abstract or not accessible from its assembly.
/// </remarks>
[Generator]
public sealed class DataAssetGenerator : IIncrementalGenerator
{
    const string DataAssetName = "Turian.Engine.Core.DataAsset";
    const string AssetName = "Turian.Engine.Core.Asset";
    const string JsonIgnoreName = "System.Text.Json.Serialization.JsonIgnoreAttribute";
    const string JsonIncludeName = "System.Text.Json.Serialization.JsonIncludeAttribute";
    const string SerializeInlineName = "Turian.SerializeInlineAttribute";
    const string ObservableName = "Turian.ObservableAttribute";

    const string Json = "global::System.Text.Json";
    const string Core = "global::Turian.Engine.Core";

    static readonly SymbolDisplayFormat typeFormat = SymbolDisplayFormat.FullyQualifiedFormat;

    static readonly SymbolDisplayFormat nullableTypeFormat = SymbolDisplayFormat.FullyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <inheritdoc />
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var types = context.SyntaxProvider.CreateSyntaxProvider(
                static (node, _) => node is ClassDeclarationSyntax { BaseList: not null },
                static (syntax, token) =>
                    syntax.SemanticModel.GetDeclaredSymbol(syntax.Node, token) as INamedTypeSymbol)
            .Where(static type => type is not null && DerivesFromDataAsset(type))
            .Select(static (type, _) => Describe(type!))
            .Where(static model => model is not null)
            .Collect();

        context.RegisterSourceOutput(types, static (output, models) =>
        {
            var serializable = models.Where(static m => m!.Members is not null).Distinct().ToList();
            if (serializable.Count > 0)
                output.AddSource("TurianGeneratedSerializers.g.cs",
                    SourceText.From(EmitSerializers(serializable!), Encoding.UTF8));

            foreach (var model in models.Where(static m => m!.Observables.Length > 0).Distinct())
                output.AddSource($"{model!.HintName}.Observable.g.cs",
                    SourceText.From(EmitObservables(model), Encoding.UTF8));
        });
    }

    static bool DerivesFromDataAsset(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == DataAssetName) return true;
        }

        return false;
    }

    static TypeModel? Describe(INamedTypeSymbol type)
    {
        var observables = type.GetMembers().OfType<IPropertySymbol>()
            .Where(static p => p.IsPartialDefinition && HasAttribute(p, ObservableName))
            .Select(static p => new ObservableModel(
                p.Name,
                p.Type.ToDisplayString(nullableTypeFormat),
                Keyword(p.DeclaredAccessibility),
                p.GetMethod is { } get && get.DeclaredAccessibility != p.DeclaredAccessibility
                    ? Keyword(get.DeclaredAccessibility) + " "
                    : string.Empty,
                p.SetMethod is null
                    ? null
                    : (p.SetMethod.DeclaredAccessibility != p.DeclaredAccessibility
                          ? Keyword(p.SetMethod.DeclaredAccessibility) + " "
                          : string.Empty) + (p.SetMethod.IsInitOnly ? "init" : "set")))
            .ToArray();

        var containers = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            containers.Insert(0, outer.Name);

        return new TypeModel(
            type.ToDisplayString(typeFormat),
            type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString(),
            new EquatableArray<string>([.. containers]),
            type.Name,
            DescribeMembers(type),
            new EquatableArray<ObservableModel>(observables));
    }

    /// <summary>The serialized members in reflection order, or null when the type keeps the reflection path.</summary>
    static EquatableArray<MemberModel>? DescribeMembers(INamedTypeSymbol type)
    {
        if (type.IsAbstract || type.IsGenericType || !IsReachable(type)) return null;
        if (!type.InstanceConstructors.Any(static c =>
                c.Parameters.Length == 0 && c.DeclaredAccessibility == Microsoft.CodeAnalysis.Accessibility.Public))
            return null;

        var chain = new List<INamedTypeSymbol>();
        for (var current = type; current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
            chain.Add(current);

        var members = new List<MemberModel>();
        var names = new HashSet<string>();

        foreach (var property in chain.SelectMany(static t => t.GetMembers().OfType<IPropertySymbol>()))
        {
            if (property.IsStatic || property.IsIndexer) continue;
            if (property.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public)
            {
                if (HasAttribute(property, JsonIncludeName)) return null;
                continue;
            }

            if (property.GetMethod?.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public)
            {
                if (HasAttribute(property, JsonIncludeName)) return null;
                continue;
            }

            if (HasAttribute(property, JsonIgnoreName) || property.SetMethod is null) continue;
            if (property.SetMethod.IsInitOnly
                || property.SetMethod.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public)
                return null;

            if (!TryAdd(property.Name, property.Type, property)) return null;
        }

        foreach (var field in chain.SelectMany(static t => t.GetMembers().OfType<IFieldSymbol>()))
        {
            if (field.IsStatic || field.IsConst || field.IsImplicitlyDeclared) continue;
            if (field.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public)
            {
                if (HasAttribute(field, JsonIncludeName)) return null;
                continue;
            }

            if (HasAttribute(field, JsonIgnoreName)) continue;
            if (field.IsReadOnly) return null;
            if (!TryAdd(field.Name, field.Type, field)) return null;
        }

        return new EquatableArray<MemberModel>([.. members]);

        bool TryAdd(string name, ITypeSymbol memberType, ISymbol member)
        {
            if (!names.Add(name) || DerivesFrom(memberType, AssetName) || memberType.IsRefLikeType
                || memberType.TypeKind == TypeKind.Pointer)
                return false;

            members.Add(new MemberModel(
                name,
                memberType.ToDisplayString(typeFormat),
                !HasAttribute(member, SerializeInlineName) && IsDataAssetReference(memberType),
                FastPath(memberType)));
            return true;
        }
    }

    static bool IsReachable(INamedTypeSymbol type)
    {
        for (var current = type; current is not null; current = current.ContainingType)
        {
            if (current.DeclaredAccessibility is not (Microsoft.CodeAnalysis.Accessibility.Public
                or Microsoft.CodeAnalysis.Accessibility.Internal))
                return false;
        }

        return true;
    }

    static bool IsDataAssetReference(ITypeSymbol type)
    {
        var element = type switch
        {
            IArrayTypeSymbol array => array.ElementType,
            INamedTypeSymbol { IsGenericType: true } named
                when named.ConstructedFrom.ToDisplayString() == "System.Collections.Generic.List<T>" =>
                named.TypeArguments[0],
            _ => type,
        };
        return DerivesFrom(element, DataAssetName);
    }

    static bool DerivesFrom(ITypeSymbol type, string baseName)
    {
        for (var current = type as INamedTypeSymbol; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseName) return true;
        }

        return false;
    }

    static string? FastPath(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_Boolean => "Boolean",
        SpecialType.System_Byte => "Byte",
        SpecialType.System_SByte => "SByte",
        SpecialType.System_Int16 => "Int16",
        SpecialType.System_UInt16 => "UInt16",
        SpecialType.System_Int32 => "Int32",
        SpecialType.System_UInt32 => "UInt32",
        SpecialType.System_Int64 => "Int64",
        SpecialType.System_UInt64 => "UInt64",
        SpecialType.System_Single => "Single",
        SpecialType.System_Double => "Double",
        SpecialType.System_Decimal => "Decimal",
        SpecialType.System_String => "String",
        _ => type.ToDisplayString() == "System.Guid" ? "Guid" : null,
    };

    static bool HasAttribute(ISymbol symbol, string name) =>
        symbol.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == name);

    static string Keyword(Accessibility accessibility) => accessibility switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        _ => "private",
    };

    static string EmitSerializers(IEnumerable<TypeModel> models)
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable disable");
        code.AppendLine("internal static class TurianGeneratedSerializers");
        code.AppendLine("{");
        code.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        code.AppendLine("    internal static void Register()");
        code.AppendLine("    {");

        foreach (var model in models)
        {
            var t = model.FullName;
            var members = model.Members!.Value;
            var references = members.Where(static m => m.IsReference).ToList();

            code.AppendLine($"        global::Turian.Engine.Core.GeneratedSerializers.Register(typeof({t}),");
            code.AppendLine("            new global::Turian.Engine.Core.GeneratedSerializer(");
            code.AppendLine($"                static () => new {t}(),");

            code.AppendLine("                static (writer, value, options) =>");
            code.AppendLine("                {");
            code.AppendLine($"                    var o = ({t})value;");
            foreach (var member in members) code.Append(EmitWrite(member));
            code.AppendLine("                },");

            code.AppendLine("                static (value, property, options) =>");
            code.AppendLine("                {");
            code.AppendLine($"                    var o = ({t})value;");
            code.AppendLine("                    switch (property.Name)");
            code.AppendLine("                    {");
            foreach (var member in members) code.Append(EmitRead(member));
            code.AppendLine("                        default: return false;");
            code.AppendLine("                    }");
            code.AppendLine("                },");

            code.AppendLine("                static (value, member, memberValue) =>");
            code.AppendLine("                {");
            code.AppendLine($"                    var o = ({t})value;");
            code.AppendLine("                    switch (member)");
            code.AppendLine("                    {");
            foreach (var member in references)
                code.AppendLine($"                        case \"{member.Name}\": "
                                + $"o.{Identifier(member.Name)} = ({member.Type})memberValue; return true;");
            code.AppendLine("                        default: return false;");
            code.AppendLine("                    }");
            code.AppendLine("                },");

            code.AppendLine("                static member => member switch");
            code.AppendLine("                {");
            foreach (var member in references)
                code.AppendLine($"                    \"{member.Name}\" => typeof({member.Type}),");
            code.AppendLine("                    _ => null,");
            code.AppendLine("                }));");
        }

        code.AppendLine("    }");
        code.AppendLine("}");
        return code.ToString();
    }

    static string Identifier(string name) =>
        SyntaxFacts.GetKeywordKind(name) == SyntaxKind.None ? name : "@" + name;

    static string EmitWrite(MemberModel member)
    {
        const string indent = "                    ";
        var name = member.Name;
        var id = Identifier(name);
        var general = $"{{ writer.WritePropertyName(\"{name}\"); "
                      + $"{Json}.JsonSerializer.Serialize(writer, o.{id}, options); }}";

        if (member.IsReference)
            return $"{indent}if (!{Core}.ObjectReferences.TryWrite("
                   + $"writer, o, \"{name}\", typeof({member.Type}), o.{id}, false)) {general}\n";

        return member.FastPath switch
        {
            null => $"{indent}{general}\n",
            "Boolean" => $"{indent}writer.WriteBoolean(\"{name}\", o.{id});\n",
            "String" => $"{indent}if (o.{id} is null) writer.WriteNull(\"{name}\"); "
                        + $"else writer.WriteString(\"{name}\", o.{id});\n",
            "Guid" => $"{indent}writer.WriteString(\"{name}\", o.{id});\n",
            _ => $"{indent}writer.WriteNumber(\"{name}\", o.{id});\n",
        };
    }

    static string EmitRead(MemberModel member)
    {
        const string indent = "                        ";
        var name = member.Name;
        var id = Identifier(name);
        var general = $"{Json}.JsonSerializer.Deserialize<{member.Type}>(property.Value, options)";

        if (member.IsReference)
            return $"{indent}case \"{name}\": o.{id} = {Core}.ObjectReferences.TryRead("
                   + $"o, \"{name}\", typeof({member.Type}), property.Value, false, out var {name}Reference) "
                   + $"? ({member.Type}){name}Reference : {general}; return true;\n";

        var fast = member.FastPath switch
        {
            null => null,
            "Boolean" => $"property.Value.ValueKind is {Json}.JsonValueKind.True or {Json}.JsonValueKind.False "
                         + $"? property.Value.GetBoolean() : {general}",
            "String" => $"{IsKind("String")} ? property.Value.GetString() : {general}",
            "Guid" => $"{IsKind("String")} ? property.Value.GetGuid() : {general}",
            _ => $"{IsKind("Number")} ? property.Value.Get{member.FastPath}() : {general}",
        };

        return $"{indent}case \"{name}\": o.{id} = {fast ?? general}; return true;\n";
    }

    static string IsKind(string kind) => $"property.Value.ValueKind == {Json}.JsonValueKind.{kind}";

    static string EmitObservables(TypeModel model)
    {
        var code = new StringBuilder();
        code.AppendLine("// <auto-generated/>");
        code.AppendLine("#nullable enable");
        if (model.Namespace is not null) code.AppendLine($"namespace {model.Namespace};");

        foreach (var container in model.Containers) code.AppendLine($"partial class {container} {{");
        code.AppendLine($"partial class {model.Name}");
        code.AppendLine("{");

        foreach (var property in model.Observables)
        {
            var field = $"__observable{property.Name}";
            code.AppendLine($"    private {property.Type} {field};");
            code.AppendLine($"    {property.Accessibility} partial {property.Type} {property.Name}");
            code.AppendLine("    {");
            code.AppendLine($"        {property.GetModifier}get => {field};");
            if (property.Setter is not null)
            {
                code.AppendLine($"        {property.Setter}");
                code.AppendLine("        {");
                code.AppendLine($"            if (global::System.Collections.Generic.EqualityComparer<{property.Type}>"
                                + $".Default.Equals({field}, value)) return;");
                code.AppendLine($"            {field} = value;");
                code.AppendLine($"            NotifyChanged(\"{property.Name}\");");
                code.AppendLine("        }");
            }
            code.AppendLine("    }");
        }

        code.AppendLine("}");
        foreach (var _ in model.Containers) code.AppendLine("}");
        return code.ToString();
    }

    sealed record TypeModel(
        string FullName,
        string? Namespace,
        EquatableArray<string> Containers,
        string Name,
        EquatableArray<MemberModel>? Members,
        EquatableArray<ObservableModel> Observables)
    {
        public string HintName => (Namespace is null ? string.Empty : Namespace + ".")
                                  + string.Join(".", Containers.Concat([Name]));
    }

    sealed record MemberModel(string Name, string Type, bool IsReference, string? FastPath);

    sealed record ObservableModel(string Name, string Type, string Accessibility, string GetModifier, string? Setter);
}

/// <summary>An immutable array compared by its elements, so generator models cache correctly.</summary>
/// <typeparam name="T">The element type.</typeparam>
readonly struct EquatableArray<T>(T[] items) : System.IEquatable<EquatableArray<T>>, IEnumerable<T>
{
    readonly T[] items = items;

    public int Length => items?.Length ?? 0;

    public bool Equals(EquatableArray<T> other) => (items ?? []).SequenceEqual(other.items ?? []);

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode() =>
        (items ?? []).Aggregate(17, static (hash, item) => hash * 31 + (item?.GetHashCode() ?? 0));

    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(items ?? [])).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
