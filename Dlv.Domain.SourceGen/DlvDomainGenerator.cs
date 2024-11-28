// TODO: Refactor this whole file

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace Dlv.Domain.SourceGen;

[Generator]
public class DlvDomainGenerator: IIncrementalGenerator {
    private static readonly SymbolDisplayFormat no_generic_format = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.None); // TODO: Lazy, try to replace with symbol check

    public void Initialize(IncrementalGeneratorInitializationContext context) {
        var provider = context.SyntaxProvider.CreateSyntaxProvider(
                predicate: static (c, _) => CanSyntaxTargetForGeneration(c),
                transform: static (n, _) =>
                     GetSemanticTargetForGeneration(n))
            .Where(static m => m is not null);

        var compilationAndClasses = context.CompilationProvider.Combine(provider.Collect());

        context.RegisterSourceOutput(compilationAndClasses, static (spc, source) => Execute(source.Left, source.Right!, spc));
    }

    private static bool CanSyntaxTargetForGeneration(SyntaxNode node) {
        return node is ClassDeclarationSyntax x && x.AttributeLists.Count > 0;
    }

    private static ClassDeclarationSyntax? GetSemanticTargetForGeneration(GeneratorSyntaxContext context) {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;

        var model = context.SemanticModel;

        foreach (var attribute in classDeclaration.AttributeLists.SelectMany(static x => x.Attributes)) {
            var attributeSymbol = model.GetSymbolInfo(attribute).Symbol as IMethodSymbol;
            if (attributeSymbol?.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Dlv.Domain.Annotations.DlvDomainAttribute") {
                return classDeclaration;
            }
        }

        return null;
    }

    private const string Spaces16 = "                ";
    private const string Spaces12 = "            ";
    private const string Spaces8 = "        ";
    private static void Execute(Compilation compilation, ImmutableArray<ClassDeclarationSyntax> classes, SourceProductionContext context) {
        var fullCode = new StringBuilder();
        HashSet<string> classesForGeneration = [];

        foreach (var @class in classes) {
            var model = compilation.GetSemanticModel(@class.SyntaxTree);
            var symbol = model.GetDeclaredSymbol(@class);
            if (symbol == null) { continue; }

            classesForGeneration.Add($"{symbol.ToDisplayString(no_generic_format)}`{symbol.Arity}");
        }

        var domainErrorEnumerableSymbol = compilation.GetTypeByMetadataName(typeof(IEnumerable<>).FullName!)!.Construct(compilation.GetTypeByMetadataName("Dlv.Domain.DomainError")!);
        var stringEnumerableSymbol = compilation.GetTypeByMetadataName(typeof(IEnumerable<>).FullName!)!.Construct(compilation.GetTypeByMetadataName("System.String")!);
        foreach (var classDeclaration in classes) {
            var partial = classDeclaration.Modifiers.Any(SyntaxKind.PartialKeyword);

            if (!partial) {
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "DLVDMNSG001",
                        "Non-partial Class",
                        "Class must be partial for generation",
                        "NonPartialClass",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    classDeclaration.GetLocation()));
            }

            if (classDeclaration.Parent is ClassDeclarationSyntax) {
                context.ReportDiagnostic(Diagnostic.Create(
                    new DiagnosticDescriptor(
                        "DLVDMNSG002",
                        "Nested Class",
                        "Class cannot be nested for generation",
                        "NestedClass",
                        DiagnosticSeverity.Error,
                        isEnabledByDefault: true),
                    classDeclaration.GetLocation()));
            }

            var model = compilation.GetSemanticModel(classDeclaration.SyntaxTree);
            var memberInfo = new List<MemberInfo>();
            var memberTypeLookup = new Dictionary<string, PropertyInformation>();
            var validationMethods = new List<ValidationMethod>();
            foreach (var member in classDeclaration.Members) {
                if (member is PropertyDeclarationSyntax property) {
                    var renameAttribute = property.AttributeLists.SelectMany(static x => x.Attributes).FirstOrDefault(x => model.GetSymbolInfo(x).Symbol?.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Dlv.Domain.Annotations.DlvDomainRenameAttribute");
                    string? key;
                    // TODO: Consider case where a const is passed in
                    if (renameAttribute != null) {
                        var argumentExpression = renameAttribute.ArgumentList?.Arguments.FirstOrDefault()?.Expression;
                        if (argumentExpression is LiteralExpressionSyntax literal) {
                            if (literal.IsKind(SyntaxKind.StringLiteralExpression)) {
                                key = literal.Token.ValueText;
                            } else if (literal.IsKind(SyntaxKind.NullLiteralExpression)) {
                                key = null;
                            } else {
                                context.ReportDiagnostic(Diagnostic.Create(
                                    new DiagnosticDescriptor(
                                        "DLVDMNSG010",
                                        "Invalid argument",
                                        "Argument must be a literal string or null",
                                        "InvalidArgument",
                                        DiagnosticSeverity.Error,
                                        isEnabledByDefault: true),
                                    literal.GetLocation()));
                                key = property.Identifier.Text;
                            }
                        } else {
                            context.ReportDiagnostic(Diagnostic.Create(
                                new DiagnosticDescriptor(
                                    "DLVDMNSG010",
                                    "Invalid argument",
                                    "Argument must be a literal string or null",
                                    "InvalidArgument",
                                    DiagnosticSeverity.Error,
                                    isEnabledByDefault: true),
                                renameAttribute.GetLocation()));
                            key = property.Identifier.Text;
                        }
                    } else {
                        key = property.Identifier.Text;
                    }

                    var propertySymbol = model.GetTypeInfo(property.Type).Type;
                    if (propertySymbol == null) { continue; }

                    var interfaceSymbol = compilation.GetTypeByMetadataName("Dlv.Domain.DomainObject");
                    var listSymbol = compilation.GetTypeByMetadataName(typeof(List<>).FullName!)!;

                    var domainObject = (interfaceSymbol != null && propertySymbol.AllInterfaces.Contains(interfaceSymbol))
                        || classesForGeneration.Contains($"{propertySymbol.ToDisplayString(no_generic_format)}`{(propertySymbol as INamedTypeSymbol)?.Arity}");

                    bool domainObjectList = false;
                    string? innerType = null;
                    if (propertySymbol is INamedTypeSymbol typeSymbol && interfaceSymbol != null) {
                        if (typeSymbol.IsGenericType && typeSymbol.ConstructedFrom.Equals(listSymbol, SymbolEqualityComparer.Default)) {
                            if (typeSymbol.TypeArguments[0].AllInterfaces.Contains(interfaceSymbol)
                                || classesForGeneration.Contains($"{typeSymbol.TypeArguments[0].ToDisplayString(no_generic_format)}`{(typeSymbol.TypeArguments[0] as INamedTypeSymbol)?.Arity}")) {
                                domainObjectList = true;
                                innerType = typeSymbol.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
                            }
                        }
                    }

                    var accessors = property.AccessorList?.Accessors;
                    var setterType = SetterType.None;
                    if (accessors != null) {
                        foreach (var a in accessors) {
                            if (a.Kind() == SyntaxKind.SetAccessorDeclaration) {
                                if (model.GetDeclaredSymbol(a)?.DeclaredAccessibility != Accessibility.Private) {
                                    context.ReportDiagnostic(Diagnostic.Create(
                                        new DiagnosticDescriptor(
                                            "DLVDMNSG006",
                                            "Non-private setter",
                                            "Setter should be private in domain objects",
                                            "NonPrivateSetter",
                                            DiagnosticSeverity.Warning,
                                            isEnabledByDefault: true),
                                        a.GetLocation()));
                                }
                                setterType = SetterType.Set;
                                break;
                            } else if (a.Kind() == SyntaxKind.InitAccessorDeclaration) {
                                if (model.GetDeclaredSymbol(a)?.DeclaredAccessibility != Accessibility.Private) {
                                    context.ReportDiagnostic(Diagnostic.Create(
                                        new DiagnosticDescriptor(
                                            "DLVDMNSG006",
                                            "Non-private init",
                                            "Init should be private in domain objects",
                                            "NonPrivateSetter",
                                            DiagnosticSeverity.Warning,
                                            isEnabledByDefault: true),
                                        a.GetLocation()));
                                }
                                setterType = SetterType.Init;
                                break;
                            }
                        }
                    }

                    memberTypeLookup.Add(
                        property.Identifier.Text,
                        new PropertyInformation {
                            DomainObjectType = domainObject ? DomainObjectType.Object : (domainObjectList ? DomainObjectType.List : DomainObjectType.None),
                            Type = propertySymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                            Name = key,
                        }
                    );

                    memberInfo.Add(new MemberInfo {
                        DomainObjectType = domainObject ? DomainObjectType.Object : (domainObjectList ? DomainObjectType.List : DomainObjectType.None),
                        Type = innerType ?? propertySymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                        //Type = propertySymbol.ToMinimalDisplayString(model, property.SpanStart),
                        Identifier = property.Identifier.Text,
                        Name = key,
                        SetterType = setterType,
                    });
                } else if (member is MethodDeclarationSyntax method) {
                    var hasValidationAttribute = false;
                    foreach (var attr in method.AttributeLists.SelectMany(static x => x.Attributes)) {
                        if (model.GetSymbolInfo(attr).Symbol?.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == "global::Dlv.Domain.Annotations.DlvDomainValidationAttribute") {
                            hasValidationAttribute = true;
                            break;
                        }
                    }
                    if (!hasValidationAttribute) { continue; }

                    if (!method.Modifiers.Any(SyntaxKind.StaticKeyword)) {
                        context.ReportDiagnostic(Diagnostic.Create(
                            new DiagnosticDescriptor(
                                "DLVDMNSG007",
                                "Non-static validation method",
                                "Validation method must be static",
                                "NonStaticValidationMethod",
                                DiagnosticSeverity.Error,
                                isEnabledByDefault: true),
                            method.GetLocation()));
                    }

                    var methodSymbol = model.GetDeclaredSymbol(method);
                    if (methodSymbol != null) {
                        if (methodSymbol.ReturnType.Equals(domainErrorEnumerableSymbol, SymbolEqualityComparer.Default)
                            || methodSymbol.ReturnType.AllInterfaces.Any(x => x.Equals(domainErrorEnumerableSymbol, SymbolEqualityComparer.Default))) {
                            validationMethods.Add(new ValidationMethod {
                               Method = methodSymbol,
                               ReturnType = ValidationReturnType.DomainErrorEnumerable,
                            });
                        } else if (methodSymbol.ReturnType.Equals(stringEnumerableSymbol, SymbolEqualityComparer.Default)
                            || methodSymbol.ReturnType.AllInterfaces.Any(x => x.Equals(stringEnumerableSymbol, SymbolEqualityComparer.Default))) {
                            validationMethods.Add(new ValidationMethod {
                                Method = methodSymbol,
                                ReturnType = ValidationReturnType.StringEnumerable,
                            });
                        } else {
                            context.ReportDiagnostic(Diagnostic.Create(
                                new DiagnosticDescriptor(
                                    "DLVDMNSG008",
                                    "Return type",
                                    "Return type of domain validation must implement IEnumerable<DomainError> or IEnumerable<string>",
                                    "InvalidValidationMethodReturn",
                                    DiagnosticSeverity.Error,
                                    isEnabledByDefault: true),
                                method.ReturnType.GetLocation()));
                        }

                        if (methodSymbol.Parameters.Length == 0) {
                            context.ReportDiagnostic(Diagnostic.Create(
                                new DiagnosticDescriptor(
                                    "DLVDMNSG009",
                                    "Parameterless validator",
                                    "Validation function must receive at least one parameter",
                                    "ParameterlessValidator",
                                    DiagnosticSeverity.Error,
                                    isEnabledByDefault: true),
                                method.GetLocation()));
                            return;
                        }
                    }
                }
            }

            foreach (var param in validationMethods.SelectMany(static x => x.Method.Parameters)) {
                if (memberTypeLookup.TryGetValue(param.Name, out var type)) {
                    if (type.Type != param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)) {
                        context.ReportDiagnostic(Diagnostic.Create(
                            new DiagnosticDescriptor(
                                "DLVDMNSG004",
                                "Type mismatch",
                                $"Type mismatch for member {param.Name}",
                                "TypeMismatch",
                                DiagnosticSeverity.Error,
                                isEnabledByDefault: true),
                            param.Locations.FirstOrDefault()));
                    }
                } else {
                    context.ReportDiagnostic(Diagnostic.Create(
                        new DiagnosticDescriptor(
                            "DLVDMNSG005",
                            "Member not found",
                            $"Member {param.Name} could not be resolved",
                            "MemberNotFound",
                            DiagnosticSeverity.Error,
                            isEnabledByDefault: true),
                        param.Locations.FirstOrDefault()));
                    return;
                }
            }

            var myNamespace = classDeclaration.GetNamespace(); // TODO: Possibly replace with classSymbol.ContainingNamespace

            var className = GetFullName(classDeclaration);

            string innerCode = $$"""
                    {{(classDeclaration.Modifiers.Any(static x => x.Kind() == SyntaxKind.InternalKeyword) ? "internal" : "public")}} partial class {{className}} : global::Dlv.Domain.DomainObject
                    {
                        private {{classDeclaration?.Identifier.Text}}() { }

                        public static global::Dlv.Domain.DomainResult<{{className}}> TryNew(
                            {{string.Join(',' + Environment.NewLine + Spaces12, memberInfo.Select(static x => x.ToFactoryParam()).Where(static x => x != null))}})
                        {
                            {{string.Join(Environment.NewLine + Spaces12, memberInfo.Select(static x => x.ToNullCheck()).Where(static x => x != null))}}

                            {{ToValidationCalls(validationMethods, memberInfo, className, memberTypeLookup)}}

                            return new global::Dlv.Domain.DomainResult<{{className}}>.Success(new {{className}}
                            {
                                {{string.Join(Environment.NewLine + Spaces16, memberInfo.Select(static x => x.ToFinalizerPart()).Where(static x => x != null))}}
                            });
                        }

                        public global::Dlv.Domain.DomainResult<{{className}}>.Success ToResult()
                            => new global::Dlv.Domain.DomainResult<{{className}}>.Success(this);

                        {{string.Join(Environment.NewLine + Spaces8, ToSetters(memberInfo, validationMethods, memberTypeLookup))}}
                    }
                """;

            string code = string.IsNullOrEmpty(myNamespace) ? innerCode : $$"""
                namespace {{myNamespace}}
                {
                {{innerCode}}
                }
                """;

            fullCode.Append(code).Append(Environment.NewLine);
        }

        context.AddSource("DlvDomainGenerations.g.cs", fullCode.ToString());
    }

    private static string GetFullName(ClassDeclarationSyntax classDeclaration) {
        var typeDeclaration = classDeclaration as TypeDeclarationSyntax;
        var name = typeDeclaration?.Identifier.Text;

        if (typeDeclaration?.TypeParameterList != null) {
            var generics = typeDeclaration.TypeParameterList.Parameters.Select(static p => p.Identifier.Text);
            var typeParameters = string.Join(", ", generics);
            name += $"<{typeParameters}>";
        }

        return name ?? "";
    }

    private static IEnumerable<string> ToSetters(ICollection<MemberInfo> members, ICollection<ValidationMethod> validationMethods, Dictionary<string, PropertyInformation> propertyLookup) {
        string? RaiseDomain(string name) {
            if (propertyLookup.TryGetValue(name, out var info)) {
                return info.ToDomainRaise(name);
            }
            return name;
        }

        foreach (var member in members.Where(static x => x.SetterType == SetterType.Set)) {
            StringBuilder validationMethodCalls = new();
            foreach (var validationMethod in validationMethods) {
                if (validationMethod.Method.Parameters.Any(x => x.Name == member.Identifier)) {
                    validationMethodCalls.AppendLine(Spaces12 + $"__errors__.AddRange({validationMethod.Method.Name}({string.Join(", ", validationMethod.Method.Parameters.Select(p => p.Name == member.Identifier ? RaiseDomain(p.Name) : $"this.{p.Name}"))})?.Where(static x => x != null){validationMethod.ToReturnMap(propertyLookup[validationMethod.Method.Parameters.FirstOrDefault()!.Name].Name)} ?? Enumerable.Empty<global::Dlv.Domain.DomainError>());");
                }
            }
            yield return $$"""
    public void Set{{member.Identifier}}({{member.ToFactoryParam()}})
            {
                var __errors__ = new List<global::Dlv.Domain.DomainError>();
                {{member.ToDomainObjectCheck()}}
                {{(member.IsDomainObject() ? "if (__errors__.Count != 0) { throw new global::Dlv.Domain.DomainException(__errors__); }" : null)}}

    {{validationMethodCalls}}

                if (__errors__.Count != 0) { throw new global::Dlv.Domain.DomainException(__errors__); }

                this.{{member.Identifier}} = {{RaiseDomain(member.Identifier)}};
            }
    """;
        }
    }

    private static string? ToValidationCalls(ICollection<ValidationMethod> validationMethods, ICollection<MemberInfo> members, string className, Dictionary<string, PropertyInformation> propertyLookup) {
        string? RaiseDomain(string name) {
            if (propertyLookup.TryGetValue(name, out var info)) {
                return info.ToDomainRaise(name);
            }
            return name;
        }

        var domainObjects = members.Where(static x => x.IsDomainObject()).ToList();
        if (validationMethods.Count == 0 && domainObjects.Count == 0) {
            return null;
        }
        return $$"""
var __errors__ = new List<global::Dlv.Domain.DomainError>();
            {{string.Join(Environment.NewLine + Spaces12, domainObjects.Select(static x => x.ToDomainObjectCheck()))}}
            {{(domainObjects.Count != 0 ? $"if (__errors__.Count != 0) {{ return new global::Dlv.Domain.DomainResult<{className}>.Failure(__errors__); }}" : null)}}

            {{string.Join(Environment.NewLine + Spaces12, validationMethods.Select(x => $"__errors__.AddRange({x.Method.Name}({string.Join(", ", x.Method.Parameters.Select(p => RaiseDomain(p.Name)))})?.Where(static x => x != null){x.ToReturnMap(propertyLookup[x.Method.Parameters.FirstOrDefault()!.Name].Name)} ?? Enumerable.Empty<global::Dlv.Domain.DomainError>());"))}}

            if (__errors__.Count != 0) { return new global::Dlv.Domain.DomainResult<{{className}}>.Failure(__errors__); }
""";
    }
}

internal class MemberInfo {
    public DomainObjectType DomainObjectType { get; set; }
    public string Type { get; set; } = null!;
    public string Identifier { get; set; } = null!;
    public string? Name { get; set; }
    public SetterType SetterType { get; set; } = SetterType.None;

    public bool IsDomainObject() => this.DomainObjectType != DomainObjectType.None;

    public string? ToFactoryParam() {
        if (this.SetterType != SetterType.None) {
            return this.DomainObjectType switch {
                DomainObjectType.Object => $"global::Dlv.Domain.DomainResult<{this.Type}> {this.Identifier}",
                DomainObjectType.List => $"global::System.Collections.Generic.IEnumerable<global::Dlv.Domain.DomainResult<{this.Type}>> {this.Identifier}",
                _ => $"{this.Type} {this.Identifier}",
            };
        }
        return null;
    }

    public string? ToNullCheck() {
        if (this.DomainObjectType != DomainObjectType.None && this.SetterType != SetterType.None) {
            return $"if ({this.Identifier} == null) {{ throw new ArgumentNullException(nameof({this.Identifier})); }}";
        }
        return null;
    }

    public string? ToDomainObjectCheck() {
        return this.DomainObjectType switch {
            DomainObjectType.Object => $$"""
        if ({{this.Identifier}} is global::Dlv.Domain.DomainResult<{{this.Type}}>.Failure)
                    {
                        __errors__.AddRange(((global::Dlv.Domain.DomainResult <{{this.Type}}>.Failure){{this.Identifier}}).Errors.Select(static x => new global::Dlv.Domain.DomainError(string.IsNullOrEmpty(x.Field) ? {{(this.Name != null ? $@"""{this.Name}""" : "null")}} : $"{{(this.Name != null ? $"{this.Name}/" : null)}}{x.Field}", x.Error)));
                    }
        """,
            DomainObjectType.List => $$"""
        foreach (var __item__ in {{this.Identifier}})
                    {
                        if (__item__ is global::Dlv.Domain.DomainResult<{{this.Type}}>.Failure)
                        {
                            __errors__.AddRange(((global::Dlv.Domain.DomainResult <{{this.Type}}>.Failure)__item__).Errors.Select(static x => new global::Dlv.Domain.DomainError(string.IsNullOrEmpty(x.Field) ? {{(this.Name != null ? $@"""{this.Name}""" : "null")}} : $"{{(this.Name != null ? $"{this.Name}/" : null)}}{x.Field}", x.Error)));
                        }
                    }
        """,
            _ => null,
        };
    }

    public string? ToFinalizerPart() {
        if (this.SetterType != SetterType.None) {
            return this.DomainObjectType switch {
                DomainObjectType.Object => $"{this.Identifier} = {this.Identifier}.Raise(),",
                DomainObjectType.List => $"{this.Identifier} = {this.Identifier}.Select(static x => x.Raise()).ToList(),",
                _ => $"{this.Identifier} = {this.Identifier},",
            };
        }
        return null;
    }
}

internal class ValidationMethod {
    public IMethodSymbol Method { get; set; } = null!;
    public ValidationReturnType ReturnType { get; set; }

    public string? ToReturnMap(string? key) {
        return this.ReturnType == ValidationReturnType.StringEnumerable ? $"?.Select(static x => new global::Dlv.Domain.DomainError({(key != null ? $@"""{key}""" : "null" )}, x))" : null;
    }
}

internal enum ValidationReturnType {
    DomainErrorEnumerable,
    StringEnumerable,
}

internal enum DomainObjectType {
    None,
    Object,
    List,
}

internal class PropertyInformation {
    public DomainObjectType DomainObjectType { get; set; }
    public string Type { get; set; } = null!;
    public string? Name { get; set; }

    public string? ToDomainRaise(string? name) {
        return this.DomainObjectType switch {
            DomainObjectType.Object => $"{name}.Raise()",
            DomainObjectType.List => $"{name}.Select(static x => x.Raise()).ToList()",
            _ => name,
        };
    }
}

internal enum SetterType {
    None,
    Init,
    Set,
}
