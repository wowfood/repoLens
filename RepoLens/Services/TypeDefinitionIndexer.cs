using System.Globalization;
using System.Text.Json;
using DevContext.Core;
using DevContext.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace DevContext.Services;

internal static class TypeDefinitionIndexer
{
    private static readonly SymbolDisplayFormat TypeNameFormat =
        SymbolDisplayFormat.CSharpErrorMessageFormat.WithMiscellaneousOptions(
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier
            | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly string[] ModifierOrder =
    [
        "file", "new", "static", "abstract", "virtual", "override", "sealed", "readonly",
        "ref", "const", "required", "async", "extern", "unsafe", "partial"
    ];

    public static async Task<IReadOnlyList<TypeDefinitionRecord>> BuildAsync(
        string repositoryRoot,
        RepositoryIndex repository,
        IReadOnlyDictionary<string, CSharpCompilation> compilations,
        IReadOnlyList<SymbolRecord> symbols,
        int maxParallelism,
        CancellationToken cancellationToken)
    {
        var symbolLookup = symbols
            .Where(IsTypeSymbol)
            .GroupBy(
                symbol => $"{symbol.Project}\0{symbol.SemanticName}",
                StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderBy(symbol => symbol.File, StringComparer.Ordinal)
                    .ThenBy(symbol => symbol.Line)
                    .First(),
                StringComparer.Ordinal);
        var projectFragments = await ParallelWork.SelectAsync(
            repository.Projects,
            maxParallelism,
            async (project, token) =>
            {
                var fragments = new List<TypeDefinitionRecord>();
                if (!compilations.TryGetValue(project.Path, out var compilation))
                {
                    return fragments;
                }

                foreach (var tree in compilation.SyntaxTrees)
                {
                    token.ThrowIfCancellationRequested();
                    var root = await tree.GetRootAsync(token);
                    var model = compilation.GetSemanticModel(tree, true);
                    var file = NormalizeRelative(repositoryRoot, tree.FilePath);
                    foreach (var declaration in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                    {
                        if (model.GetDeclaredSymbol(declaration, token) is not INamedTypeSymbol typeSymbol)
                        {
                            continue;
                        }

                        var semanticName = typeSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                        if (!symbolLookup.TryGetValue($"{project.Path}\0{semanticName}", out var symbol))
                        {
                            continue;
                        }

                        fragments.Add(BuildFragment(
                            repositoryRoot,
                            project,
                            file,
                            declaration,
                            typeSymbol,
                            symbol,
                            token));
                    }
                }

                return fragments;
            },
            cancellationToken);
        var fragments = projectFragments.SelectMany(project => project).ToArray();

        return fragments
            .GroupBy(fragment => fragment.SymbolIdentity, StringComparer.Ordinal)
            .Select(MergeFragments)
            .OrderBy(definition => definition.Project, StringComparer.Ordinal)
            .ThenBy(definition => definition.Declarations[0].File, StringComparer.Ordinal)
            .ThenBy(definition => definition.Declarations[0].Line)
            .ThenBy(definition => definition.SymbolIdentity, StringComparer.Ordinal)
            .ToArray();
    }

    private static TypeDefinitionRecord BuildFragment(
        string repositoryRoot,
        ProjectRecord project,
        string file,
        BaseTypeDeclarationSyntax declaration,
        INamedTypeSymbol typeSymbol,
        SymbolRecord symbol,
        CancellationToken cancellationToken)
    {
        var members = typeSymbol.GetMembers()
            .Where(IsDeclaredMember)
            .Select(member => BuildMember(
                repositoryRoot,
                project,
                declaration,
                member,
                cancellationToken));

        return new TypeDefinitionRecord(
            symbol.Identity,
            symbol.Kind,
            symbol.Name,
            symbol.SemanticName ?? symbol.Name,
            symbol.Namespace,
            symbol.ContainingType,
            project.Path,
            AccessibilityOf(typeSymbol.DeclaredAccessibility),
            ModifiersOf(declaration.Modifiers, typeSymbol),
            TypeParametersOf(typeSymbol.TypeParameters),
            AttributesOf(typeSymbol),
            typeSymbol.BaseType is { SpecialType: not SpecialType.System_Object } baseType
                ? TypeName(baseType)
                : null,
            typeSymbol.Interfaces.Select(TypeName).Order(StringComparer.Ordinal).ToArray(),
            members.Where(member => member is not null).Cast<MemberDefinitionRecord>().ToArray(),
            [new SourceLocationRecord(file, LineOf(declaration)) { EndLine = EndLineOf(declaration) }]);
    }

    private static MemberDefinitionRecord? BuildMember(
        string repositoryRoot,
        ProjectRecord project,
        BaseTypeDeclarationSyntax containingDeclaration,
        ISymbol declaredSymbol,
        CancellationToken cancellationToken)
    {
        var syntaxReference = declaredSymbol.DeclaringSyntaxReferences
            .Where(reference => ReferenceEquals(reference.SyntaxTree, containingDeclaration.SyntaxTree)
                                && containingDeclaration.FullSpan.Contains(reference.Span))
            .OrderBy(reference => reference.Span.Start)
            .FirstOrDefault();
        if (syntaxReference is null)
        {
            return null;
        }

        var declaration = syntaxReference.GetSyntax(cancellationToken);
        var kind = MemberKind(declaredSymbol);
        var semanticName = declaredSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
        var identity = Hashing.Text(string.Join('|', project.Path, "member", kind, semanticName));
        var declaredType = DeclaredTypeOf(declaredSymbol);
        var syntaxModifiers = declaration switch
        {
            MemberDeclarationSyntax member => member.Modifiers,
            VariableDeclaratorSyntax variable when variable.Parent?.Parent is MemberDeclarationSyntax member => member.Modifiers,
            _ => default
        };
        var parameters = declaredSymbol switch
        {
            IMethodSymbol method => ParametersOf(method.Parameters),
            IPropertySymbol property => ParametersOf(property.Parameters),
            _ => []
        };
        var typeParameters = declaredSymbol is IMethodSymbol genericMethod
            ? TypeParametersOf(genericMethod.TypeParameters)
            : [];
        var file = declaration.SyntaxTree.FilePath.Length == 0
            ? string.Empty
            : NormalizeRelative(repositoryRoot, declaration.SyntaxTree.FilePath);

        return new MemberDefinitionRecord(
            identity,
            kind,
            MemberName(declaredSymbol),
            semanticName,
            AccessibilityOf(declaredSymbol.DeclaredAccessibility),
            ModifiersOf(syntaxModifiers, declaredSymbol),
            declaredType is null ? null : TypeName(declaredType),
            declaredType is null ? null : NullabilityOf(declaredType.NullableAnnotation),
            AccessorsOf(declaredSymbol),
            parameters,
            typeParameters,
            AttributesOf(declaredSymbol),
            new SourceLocationRecord(file, LineOf(declaration)) { EndLine = EndLineOf(declaration) });
    }

    private static bool IsDeclaredMember(ISymbol symbol)
    {
        if (symbol.IsImplicitlyDeclared)
        {
            return false;
        }

        return symbol switch
        {
            IMethodSymbol
            {
                AssociatedSymbol: null, MethodKind: MethodKind.Constructor
                or MethodKind.StaticConstructor
                or MethodKind.Destructor
                or MethodKind.Ordinary
                or MethodKind.UserDefinedOperator
                or MethodKind.Conversion
            } => true,
            IPropertySymbol or IFieldSymbol or IEventSymbol => true,
            _ => false
        };
    }

    private static TypeDefinitionRecord MergeFragments(IGrouping<string, TypeDefinitionRecord> group)
    {
        var ordered = group
            .OrderBy(fragment => fragment.Declarations[0].File, StringComparer.Ordinal)
            .ThenBy(fragment => fragment.Declarations[0].Line)
            .ToArray();
        var first = ordered[0];
        return first with
        {
            Modifiers = OrderModifiers(ordered.SelectMany(fragment => fragment.Modifiers)),
            Attributes = DistinctAttributes(ordered.SelectMany(fragment => fragment.Attributes)),
            Interfaces = ordered.SelectMany(fragment => fragment.Interfaces)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray(),
            Members = ordered.SelectMany(fragment => fragment.Members)
                .DistinctBy(member => member.Identity, StringComparer.Ordinal)
                .OrderBy(member => member.Location.File, StringComparer.Ordinal)
                .ThenBy(member => member.Location.Line)
                .ThenBy(member => member.Identity, StringComparer.Ordinal)
                .ToArray(),
            Declarations = ordered.SelectMany(fragment => fragment.Declarations)
                .Distinct()
                .OrderBy(location => location.File, StringComparer.Ordinal)
                .ThenBy(location => location.Line)
                .ToArray()
        };
    }

    private static IReadOnlyList<ParameterDefinitionRecord> ParametersOf(
        IEnumerable<IParameterSymbol> parameters) => parameters
        .Select(parameter => new ParameterDefinitionRecord(
            parameter.Name,
            TypeName(parameter.Type),
            NullabilityOf(parameter.NullableAnnotation),
            parameter.RefKind == RefKind.None ? string.Empty : parameter.RefKind.ToString().ToLowerInvariant(),
            parameter.IsParams,
            parameter.IsOptional,
            DefaultValueOf(parameter),
            AttributesOf(parameter)))
        .ToArray();

    private static IReadOnlyList<TypeParameterDefinitionRecord> TypeParametersOf(
        IEnumerable<ITypeParameterSymbol> typeParameters) => typeParameters
        .Select(parameter => new TypeParameterDefinitionRecord(
            parameter.Name,
            parameter.Variance.ToString().ToLowerInvariant(),
            ConstraintsOf(parameter)))
        .ToArray();

    private static IReadOnlyList<string> ConstraintsOf(ITypeParameterSymbol parameter)
    {
        var constraints = new List<string>();
        if (parameter.HasUnmanagedTypeConstraint)
        {
            constraints.Add("unmanaged");
        }
        else if (parameter.HasValueTypeConstraint)
        {
            constraints.Add("struct");
        }
        else if (parameter.HasReferenceTypeConstraint)
        {
            constraints.Add(parameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated
                ? "class?"
                : "class");
        }

        if (parameter.HasNotNullConstraint)
        {
            constraints.Add("notnull");
        }

        constraints.AddRange(parameter.ConstraintTypes.Select(TypeName).Order(StringComparer.Ordinal));
        if (parameter.HasConstructorConstraint)
        {
            constraints.Add("new()");
        }

        return constraints;
    }

    private static IReadOnlyList<AttributeDefinitionRecord> AttributesOf(ISymbol symbol) =>
        DistinctAttributes(symbol.GetAttributes().Select(attribute =>
        {
            var typeName = attribute.AttributeClass is null
                ? "(unresolved)"
                : TypeName(attribute.AttributeClass);
            var arguments = attribute.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax syntax
                ? syntax.ArgumentList?.Arguments.Select(argument => argument.ToString()).ToArray() ?? []
                : attribute.ConstructorArguments.Select(FormatConstant)
                    .Concat(attribute.NamedArguments.Select(argument => $"{argument.Key}={FormatConstant(argument.Value)}"))
                    .ToArray();
            return new AttributeDefinitionRecord(typeName, arguments);
        }));

    private static IReadOnlyList<AttributeDefinitionRecord> DistinctAttributes(
        IEnumerable<AttributeDefinitionRecord> attributes) => attributes
        .DistinctBy(
            attribute => $"{attribute.TypeName}\0{string.Join("\0", attribute.Arguments)}",
            StringComparer.Ordinal)
        .OrderBy(attribute => attribute.TypeName, StringComparer.Ordinal)
        .ThenBy(attribute => string.Join("\0", attribute.Arguments), StringComparer.Ordinal)
        .ToArray();

    private static IReadOnlyList<string> AccessorsOf(ISymbol symbol) => symbol switch
    {
        IPropertySymbol property => new[]
            {
                AccessorOf(property.GetMethod, "get", property.DeclaredAccessibility),
                AccessorOf(
                    property.SetMethod,
                    property.SetMethod?.IsInitOnly == true ? "init" : "set",
                    property.DeclaredAccessibility)
            }
            .Where(accessor => accessor is not null)
            .Cast<string>()
            .ToArray(),
        IEventSymbol @event => new[]
            {
                AccessorOf(@event.AddMethod, "add", @event.DeclaredAccessibility),
                AccessorOf(@event.RemoveMethod, "remove", @event.DeclaredAccessibility)
            }
            .Where(accessor => accessor is not null)
            .Cast<string>()
            .ToArray(),
        _ => []
    };

    private static string? AccessorOf(
        IMethodSymbol? accessor,
        string name,
        Accessibility containingAccessibility)
    {
        if (accessor is null)
        {
            return null;
        }

        return accessor.DeclaredAccessibility is Accessibility.NotApplicable
               || accessor.DeclaredAccessibility == containingAccessibility
            ? name
            : $"{AccessibilityOf(accessor.DeclaredAccessibility)} {name}";
    }

    private static ITypeSymbol? DeclaredTypeOf(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.Destructor } => null,
        IMethodSymbol method => method.ReturnType,
        IPropertySymbol property => property.Type,
        IFieldSymbol field => field.Type,
        IEventSymbol @event => @event.Type,
        _ => null
    };

    private static string MemberKind(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } => "constructor",
        IMethodSymbol { MethodKind: MethodKind.Destructor } => "destructor",
        IMethodSymbol { MethodKind: MethodKind.UserDefinedOperator } => "operator",
        IMethodSymbol { MethodKind: MethodKind.Conversion } => "conversion-operator",
        IMethodSymbol => "method",
        IPropertySymbol { IsIndexer: true } => "indexer",
        IPropertySymbol => "property",
        IFieldSymbol { ContainingType.TypeKind: TypeKind.Enum } => "enum-member",
        IFieldSymbol => "field",
        IEventSymbol => "event",
        _ => "member"
    };

    private static string MemberName(ISymbol symbol) => symbol switch
    {
        IMethodSymbol { MethodKind: MethodKind.Constructor or MethodKind.StaticConstructor } method =>
            method.ContainingType.Name,
        IMethodSymbol { MethodKind: MethodKind.Destructor } method => $"~{method.ContainingType.Name}",
        IPropertySymbol { IsIndexer: true } => "this[]",
        _ => symbol.Name
    };

    private static IReadOnlyList<string> ModifiersOf(SyntaxTokenList syntaxModifiers, ISymbol symbol)
    {
        var modifiers = syntaxModifiers
            .Select(modifier => modifier.ValueText)
            .Where(modifier => modifier is not ("public" or "private" or "protected" or "internal"))
            .ToList();
        if (symbol.IsStatic) modifiers.Add("static");
        if (symbol.IsAbstract) modifiers.Add("abstract");
        if (symbol.IsVirtual) modifiers.Add("virtual");
        if (symbol.IsOverride) modifiers.Add("override");
        if (symbol.IsSealed) modifiers.Add("sealed");
        switch (symbol)
        {
            case INamedTypeSymbol { IsReadOnly: true }:
                modifiers.Add("readonly");
                break;
            case INamedTypeSymbol { IsRefLikeType: true }:
                modifiers.Add("ref");
                break;
            case IFieldSymbol { IsReadOnly: true }:
                modifiers.Add("readonly");
                break;
        }

        if (symbol is IFieldSymbol { IsConst: true }) modifiers.Add("const");
        if (symbol is IFieldSymbol { IsRequired: true } or IPropertySymbol { IsRequired: true }) modifiers.Add("required");
        if (symbol is IMethodSymbol { IsAsync: true }) modifiers.Add("async");
        if (symbol is IMethodSymbol { IsExtern: true }) modifiers.Add("extern");
        return OrderModifiers(modifiers);
    }

    private static IReadOnlyList<string> OrderModifiers(IEnumerable<string> modifiers) => modifiers
        .Distinct(StringComparer.Ordinal)
        .OrderBy(modifier => Array.IndexOf(ModifierOrder, modifier) is var index && index >= 0
            ? index
            : int.MaxValue)
        .ThenBy(modifier => modifier, StringComparer.Ordinal)
        .ToArray();

    private static string AccessibilityOf(Accessibility accessibility) => accessibility switch
    {
        Accessibility.Public => "public",
        Accessibility.Internal => "internal",
        Accessibility.Private => "private",
        Accessibility.Protected => "protected",
        Accessibility.ProtectedAndInternal => "private-protected",
        Accessibility.ProtectedOrInternal => "protected-internal",
        _ => "not-applicable"
    };

    private static string TypeName(ITypeSymbol type) => type.ToDisplayString(TypeNameFormat);

    private static string NullabilityOf(NullableAnnotation annotation) => annotation switch
    {
        NullableAnnotation.Annotated => "annotated",
        NullableAnnotation.NotAnnotated => "not-annotated",
        _ => "oblivious"
    };

    private static string? DefaultValueOf(IParameterSymbol parameter)
    {
        var syntaxValue = parameter.DeclaringSyntaxReferences
            .Select(reference => reference.GetSyntax())
            .OfType<ParameterSyntax>()
            .Select(syntax => syntax.Default?.Value.ToString())
            .FirstOrDefault(value => value is not null);
        if (syntaxValue is not null)
        {
            return syntaxValue;
        }

        return parameter.HasExplicitDefaultValue ? FormatValue(parameter.ExplicitDefaultValue) : null;
    }

    private static string FormatConstant(TypedConstant constant)
    {
        if (constant.Kind == TypedConstantKind.Array)
        {
            return $"[{string.Join(", ", constant.Values.Select(FormatConstant))}]";
        }

        return FormatValue(constant.Value);
    }

    private static string FormatValue(object? value) => value switch
    {
        null => "null",
        string text => JsonSerializer.Serialize(text),
        char character => $"'{character}'",
        bool boolean => boolean ? "true" : "false",
        ITypeSymbol type => $"typeof({TypeName(type)})",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
        _ => value.ToString() ?? string.Empty
    };

    private static bool IsTypeSymbol(SymbolRecord symbol) => symbol.Kind is
        "class" or "record" or "struct" or "interface" or "enum";

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private static int EndLineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().EndLinePosition.Line + 1;

    private static string NormalizeRelative(string repositoryRoot, string path) =>
        path.StartsWith("generated://", StringComparison.Ordinal)
            ? path
            : Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/');
}
