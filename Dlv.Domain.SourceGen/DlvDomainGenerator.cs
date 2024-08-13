// TODO: Refactor this whole file

using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;
using System.Text;

namespace Dlv.Domain.SourceGen;

[Generator]
public class DlvDomainGenerator: IIncrementalGenerator {
    private static SymbolDisplayFormat NoGenericFormat = new(
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
            if (attributeSymbol?.ContainingType.ToDisplayString() == "Dlv.Domain.Annotations.DlvDomainAttribute") {
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

            classesForGeneration.Add($"{symbol.ToDisplayString(NoGenericFormat)}`{symbol.Arity}");
        }

        var enumerableSymbol = compilation.GetTypeByMetadataName(typeof(IEnumerable<>).FullName)!.Construct(compilation.GetTypeByMetadataName("Dlv.Domain.DomainError")!)!;
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
            var validationMethods = new List<IMethodSymbol>();
            foreach (var member in classDeclaration.Members) {
                if (member is PropertyDeclarationSyntax property) {
                    var propertySymbol = model.GetTypeInfo(property.Type).Type;
                    if (propertySymbol == null) { continue; }

                    var interfaceSymbol = compilation.GetTypeByMetadataName("Dlv.Domain.DomainObject");

                    var domainObject = (interfaceSymbol != null && propertySymbol.AllInterfaces.Contains(interfaceSymbol))
                        || classesForGeneration.Contains($"{propertySymbol.ToDisplayString(NoGenericFormat)}`{(propertySymbol as INamedTypeSymbol)?.Arity}");

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

                    memberTypeLookup.Add(property.Identifier.Text, new PropertyInformation { DomainObject = domainObject, Type = propertySymbol.ToDisplayString() });

                    memberInfo.Add(new MemberInfo {
                        DomainObject = domainObject,
                        Type = propertySymbol.ToDisplayString(),
                        Identifier = property.Identifier.Text,
                        SetterType = setterType,
                    });
                } else if (member is MethodDeclarationSyntax method) {
                    var hasValidationAttribute = false;
                    foreach (var attr in method.AttributeLists.SelectMany(static x => x.Attributes)) {
                        if (model.GetSymbolInfo(attr).Symbol?.ContainingType.ToDisplayString() == "Dlv.Domain.Annotations.DlvDomainValidationAttribute") {
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
                        if (!methodSymbol.ReturnType.Equals(enumerableSymbol, SymbolEqualityComparer.Default)
                            && !methodSymbol.ReturnType.AllInterfaces.Any(x => x.Equals(enumerableSymbol, SymbolEqualityComparer.Default))) {
                            context.ReportDiagnostic(Diagnostic.Create(
                                new DiagnosticDescriptor(
                                    "DLVDMNSG008",
                                    "Return type",
                                    "Return type of domain validation must implement IEnumerable<DomainError>",
                                    "InvalidValidationMethodReturn",
                                    DiagnosticSeverity.Error,
                                    isEnabledByDefault: true),
                                method.ReturnType.GetLocation()));
                        }
                        validationMethods.Add(methodSymbol);
                    }
                }
            }

            foreach (var param in validationMethods.SelectMany(static x => x.Parameters)) {
                if (memberTypeLookup.TryGetValue(param.Name, out var type)) {
                    if (type.Type != param.Type.ToDisplayString()) {
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
                }
            }

            var myNamespace = classDeclaration.GetNamespace(); // TODO: Possibly replace with classSymbol.ContainingNamespace

            var className = GetFullName(classDeclaration);

            string innerCode = $$"""
                    {{(classDeclaration.Modifiers.Any(static x => x.Kind() == SyntaxKind.InternalKeyword) ? "internal" : "public")}} partial class {{className}} : Dlv.Domain.DomainObject
                    {
                        private {{classDeclaration?.Identifier.Text}}() { }

                        public static Dlv.Domain.DomainResult<{{className}}> TryNew(
                            {{string.Join(',' + Environment.NewLine + Spaces12, memberInfo.Select(static x => x.ToFactoryParam()).Where(static x => x != null))}})
                        {
                            {{string.Join(Environment.NewLine + Spaces12, memberInfo.Select(static x => x.ToNullCheck()).Where(static x => x != null))}}

                            {{ToValidationCalls(validationMethods, memberInfo, className, memberTypeLookup)}}

                            return new Dlv.Domain.DomainResult<{{className}}>.Success(new {{className}}
                            {
                                {{string.Join(Environment.NewLine + Spaces16, memberInfo.Select(static x => x.ToFinalizerPart()).Where(static x => x != null))}}
                            });
                        }

                        public Dlv.Domain.DomainResult<{{className}}>.Success ToResult()
                            => new Dlv.Domain.DomainResult<{{className}}>.Success(this);

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

    private static IEnumerable<string> ToSetters(ICollection<MemberInfo> members, ICollection<IMethodSymbol> validationMethods, Dictionary<string, PropertyInformation> propertyLookup) {
        string? RaiseDomain(string name) {
            if (propertyLookup.TryGetValue(name, out var info)) {
                if (info.DomainObject) {
                    return ".Raise()";
                }
            }
            return null;
        }

        foreach (var member in members.Where(static x => x.SetterType == SetterType.Set)) {
            StringBuilder validationMethodCalls = new();
            foreach (var validationMethod in validationMethods) {
                if (validationMethod.Parameters.Any(x => x.Name == member.Identifier)) {
                    validationMethodCalls.AppendLine(Spaces12 + $"__errors__.AddRange({validationMethod.Name}({string.Join(", ", validationMethod.Parameters.Select(p => p.Name == member.Identifier ? $"{p.Name}{RaiseDomain(p.Name)}" : $"this.{p.Name}"))}) ?? Enumerable.Empty<Dlv.Domain.DomainError>());");
                }
            }
            yield return $$"""
    public void Set{{member.Identifier}}({{member.ToFactoryParam()}})
            {
                var __errors__ = new List<Dlv.Domain.DomainError>();
                {{member.ToDomainObjectCheck()}}
                {{(member.DomainObject ? "if (__errors__.Count != 0) { throw new Dlv.Domain.DomainException(__errors__); }" : null)}}

    {{validationMethodCalls}}

                if (__errors__.Count != 0) { throw new Dlv.Domain.DomainException(__errors__); }
            }
    """;
        }
    }

    private static string? ToValidationCalls(ICollection<IMethodSymbol> validationMethods, ICollection<MemberInfo> members, string className, Dictionary<string, PropertyInformation> propertyLookup) {
        string? RaiseDomain(string name) {
            if (propertyLookup.TryGetValue(name, out var info)) {
                if (info.DomainObject) {
                    return ".Raise()";
                }
            }
            return null;
        }

        var domainObjects = members.Where(static x => x.DomainObject).ToList();
        if (validationMethods.Count == 0 && domainObjects.Count == 0) {
            return null;
        }
        return $$"""
var __errors__ = new List<Dlv.Domain.DomainError>();
            {{string.Join(Environment.NewLine + Spaces12, domainObjects.Select(static x => x.ToDomainObjectCheck()))}}
            {{(domainObjects.Count != 0 ? $"if (__errors__.Count != 0) {{ return new Dlv.Domain.DomainResult<{className}>.Failure(__errors__); }}" : null)}}

            {{string.Join(Environment.NewLine + Spaces12, validationMethods.Select(x => $"__errors__.AddRange({x.Name}({string.Join(", ", x.Parameters.Select(p => $"{p.Name}{RaiseDomain(p.Name)}"))}) ?? Enumerable.Empty<Dlv.Domain.DomainError>());"))}}

            if (__errors__.Count != 0) { return new Dlv.Domain.DomainResult<{{className}}>.Failure(__errors__); }
""";
    }
}

internal class MemberInfo {
    public bool DomainObject { get; set; }
    public string Type { get; set; } = null!;
    public string Identifier { get; set; } = null!;
    public SetterType SetterType { get; set; } = SetterType.None;

    public string? ToFactoryParam() {
        if (this.SetterType != SetterType.None) {
            if (this.DomainObject) {
                return $"Dlv.Domain.DomainResult<{this.Type}> {this.Identifier}";
            } else {
                return $"{this.Type} {this.Identifier}";
            }
        }
        return null;
    }

    public string? ToNullCheck() {
        if (this.DomainObject && this.SetterType != SetterType.None) {
            return $"if ({this.Identifier} == null) {{ throw new ArgumentNullException(nameof({this.Identifier})); }}";
        }
        return null;
    }

    public string? ToDomainObjectCheck() {
        if (this.DomainObject) {
            return $$"""
        if ({{this.Identifier}} is Dlv.Domain.DomainResult<{{this.Type}}>.Failure)
                    {
                        __errors__.AddRange(((Dlv.Domain.DomainResult <{{this.Type}}>.Failure){{this.Identifier}}).Errors.Select(static x => new Dlv.Domain.DomainError(string.IsNullOrEmpty(x.Field) ? nameof({{this.Identifier}}) : $"{nameof({{this.Identifier}})}/{x.Field}", x.Error)));
                    }
        """;
        }
        return null;
    }

    public string? ToFinalizerPart() {
        if (this.SetterType != SetterType.None) {
            if (this.DomainObject) {
                return $"{this.Identifier} = {this.Identifier}.Raise(),";
            } else {
                return $"{this.Identifier} = {this.Identifier},";
            }
        }
        return null;
    }
}

internal class PropertyInformation {
    public bool DomainObject { get; set; }
    public string Type { get; set; } = null!;
}

internal enum SetterType {
    None,
    Init,
    Set,
}
