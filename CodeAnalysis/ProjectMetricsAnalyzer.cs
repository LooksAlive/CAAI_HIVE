using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace CAAI;

public sealed record MethodMetric(
    string Name,
    string Signature,
    int Complexity,
    int Lines,
    string FilePath,
    int StartLine,
    string Fingerprint);

public sealed record TypeMetric(
    string Name,
    string Namespace,
    string Kind,
    int MethodCount,
    int PropertyCount,
    int FieldCount,
    int Lines,
    int Complexity,
    IReadOnlyList<MethodMetric> Methods);

public sealed record NamingIssue(string Kind, string Name, string ExpectedStyle, string Location);

public sealed record DuplicateCodeGroup(IReadOnlyList<MethodMetric> Methods, int SimilarityPercent);

public sealed record ProjectMetrics(
    string ProjectName,
    int SourceFileCount,
    int LinesOfCode,
    int ClassCount,
    int StructCount,
    int InterfaceCount,
    int RecordCount,
    int EnumCount,
    int MethodCount,
    int PropertyCount,
    int FieldCount,
    int TotalComplexity,
    IReadOnlyList<TypeMetric> Types,
    IReadOnlyList<NamingIssue> NamingIssues,
    IReadOnlyList<DuplicateCodeGroup> SimilarCode)
{
    public ProjectSimilarityReport Similarities { get; init; } = ProjectSimilarityReport.Empty;
}

public static class ProjectMetricsAnalyzer
{
    private static readonly Regex PascalCase = new("^[A-Z][A-Za-z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CamelCase = new("^[a-z][A-Za-z0-9]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static async Task<ProjectMetrics> AnalyzeAsync(Project project, CancellationToken cancellationToken = default)
    {
        var compilation = await project.GetCompilationAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Could not compile project {project.Name}.");
        return await Task.Run(() => AnalyzeCompilation(project.Name, compilation, cancellationToken), cancellationToken);
    }

    private static ProjectMetrics AnalyzeCompilation(
        string projectName,
        Compilation compilation,
        CancellationToken cancellationToken)
    {
        var declarations = new Dictionary<INamedTypeSymbol, List<TypeDeclarationSyntax>>(SymbolEqualityComparer.Default);
        var namingIssues = new List<NamingIssue>();
        var sourceLines = 0;
        var sourceFileCount = 0;
        var enumCount = 0;

        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            sourceFileCount++;
            var text = tree.GetText(cancellationToken);
            sourceLines += CountNonEmptyLines(text);
            var root = tree.GetRoot(cancellationToken);
            var semanticModel = compilation.GetSemanticModel(tree);

            foreach (var declaration in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (semanticModel.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol symbol)
                    continue;
                if (!declarations.TryGetValue(symbol, out var parts))
                    declarations.Add(symbol, parts = new List<TypeDeclarationSyntax>());
                parts.Add(declaration);
                AddNamingIssue(namingIssues, "Type", symbol.Name, PascalCase, "PascalCase", declaration.GetLocation());
            }

            foreach (var enumDeclaration in root.DescendantNodes().OfType<EnumDeclarationSyntax>())
            {
                enumCount++;
                if (semanticModel.GetDeclaredSymbol(enumDeclaration, cancellationToken) is { } enumSymbol)
                    AddNamingIssue(namingIssues, "Enum", enumSymbol.Name, PascalCase, "PascalCase", enumDeclaration.GetLocation());
            }
        }

        var types = new List<TypeMetric>();
        var allMethods = new List<MethodMetric>();
        foreach (var (symbol, parts) in declarations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var methods = new Dictionary<IMethodSymbol, MethodMetric>(SymbolEqualityComparer.Default);
            var typeLines = 0;
            var typeComplexity = 0;
            foreach (var part in parts)
            {
                var text = part.SyntaxTree.GetText(cancellationToken);
                typeLines += CountNonEmptyLines(text, part.Span);
                var semanticModel = compilation.GetSemanticModel(part.SyntaxTree);
                foreach (var member in part.Members)
                {
                    if (member is not BaseMethodDeclarationSyntax methodDeclaration ||
                        semanticModel.GetDeclaredSymbol(methodDeclaration, cancellationToken) is not IMethodSymbol methodSymbol ||
                        methodSymbol.IsImplicitlyDeclared)
                        continue;

                    AddNamingIssue(namingIssues, "Method", methodSymbol.Name, PascalCase, "PascalCase", methodDeclaration.GetLocation());
                    foreach (var parameter in methodSymbol.Parameters)
                    {
                        var parameterNode = methodDeclaration.ParameterList.Parameters
                            .FirstOrDefault(candidate => candidate.Identifier.ValueText == parameter.Name);
                        AddNamingIssue(namingIssues, "Parameter", parameter.Name, CamelCase, "camelCase", parameterNode?.GetLocation() ?? methodDeclaration.GetLocation());
                    }

                    var complexity = CalculateComplexity(methodDeclaration);
                    var methodMetric = new MethodMetric(
                        methodSymbol.Name,
                        methodSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        complexity,
                        CountNonEmptyLines(text, methodDeclaration.Span),
                        part.SyntaxTree.FilePath,
                        methodDeclaration.GetLocation().GetLineSpan().StartLinePosition.Line + 1,
                        CreateFingerprint(methodDeclaration));
                    methods[methodSymbol] = methodMetric;
                }
            }

            var methodMetrics = methods.Values.OrderByDescending(method => method.Complexity).ToArray();
            typeComplexity = methodMetrics.Sum(method => method.Complexity);
            allMethods.AddRange(methodMetrics);
            var members = symbol.GetMembers().Where(member => !member.IsImplicitlyDeclared).ToArray();
            var methodCount = members.OfType<IMethodSymbol>().Count(IsSourceMethod);
            var propertyCount = members.OfType<IPropertySymbol>().Count();
            var fieldCount = members.OfType<IFieldSymbol>().Count(field => !field.IsImplicitlyDeclared);

            foreach (var field in members.OfType<IFieldSymbol>().Where(field => !field.IsImplicitlyDeclared && !field.IsConst))
            {
                var fieldStyle = field.DeclaredAccessibility == Accessibility.Private ? "_camelCase or camelCase" : "camelCase";
                if (!CamelCase.IsMatch(field.Name) && !(field.DeclaredAccessibility == Accessibility.Private && field.Name.StartsWith('_') && CamelCase.IsMatch(field.Name[1..])))
                    namingIssues.Add(new NamingIssue("Field", field.Name, fieldStyle, GetLocationLabel(field.Locations.FirstOrDefault())));
            }

            var kind = symbol.IsRecord ? "Record" : symbol.TypeKind.ToString();
            types.Add(new TypeMetric(symbol.Name, symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty, kind,
                methodCount, propertyCount, fieldCount, typeLines, typeComplexity, methodMetrics));
        }

        var sortedTypes = types.OrderByDescending(type => type.Complexity)
            .ThenByDescending(type => type.MethodCount)
            .ThenBy(type => type.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var duplicateGroups = allMethods
            .Where(method => method.Fingerprint.Length > 0)
            .GroupBy(method => method.Fingerprint, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => new DuplicateCodeGroup(group.ToArray(), 100))
            .OrderByDescending(group => group.Methods.Count)
            .ToArray();

        var allSymbols = declarations.Keys;
        return new ProjectMetrics(
            projectName,
            sourceFileCount,
            sourceLines,
            types.Count(type => type.Kind == "Class"),
            types.Count(type => type.Kind == "Struct"),
            types.Count(type => type.Kind == "Interface"),
            types.Count(type => type.Kind == "Record"),
            enumCount,
            types.Sum(type => type.MethodCount),
            types.Sum(type => type.PropertyCount),
            types.Sum(type => type.FieldCount),
            types.Sum(type => type.Complexity),
            sortedTypes,
            namingIssues.OrderBy(issue => issue.Kind, StringComparer.Ordinal).ThenBy(issue => issue.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            duplicateGroups)
        {
            Similarities = ProjectSimilarityAnalyzer.Analyze(compilation, cancellationToken: cancellationToken)
        };
    }

    private static bool IsSourceMethod(IMethodSymbol method) => !method.IsImplicitlyDeclared && method.MethodKind is
        MethodKind.Ordinary or MethodKind.Constructor or MethodKind.StaticConstructor or MethodKind.UserDefinedOperator or MethodKind.Conversion;

    private static int CalculateComplexity(SyntaxNode method)
    {
        var complexity = 1;
        foreach (var node in method.DescendantNodes())
        {
            if (node is IfStatementSyntax or ForStatementSyntax or ForEachStatementSyntax or WhileStatementSyntax or
                DoStatementSyntax or CatchClauseSyntax or ConditionalExpressionSyntax or SwitchExpressionArmSyntax)
                complexity++;
            else if (node is CaseSwitchLabelSyntax or CasePatternSwitchLabelSyntax)
                complexity++;
            else if (node is BinaryExpressionSyntax binary && binary.Kind() is
                     SyntaxKind.LogicalAndExpression or SyntaxKind.LogicalOrExpression or SyntaxKind.CoalesceExpression)
                complexity++;
        }
        return complexity;
    }

    private static string CreateFingerprint(SyntaxNode method)
    {
        var tokens = method.DescendantTokens()
            .Where(token => !token.IsMissing)
            .Select(token => token.Kind() switch
            {
                SyntaxKind.IdentifierToken => "<id>",
                SyntaxKind.NumericLiteralToken or SyntaxKind.StringLiteralToken or SyntaxKind.CharacterLiteralToken => "<literal>",
                _ => token.ValueText
            })
            .ToArray();
        return tokens.Length < 8 ? string.Empty : string.Join(' ', tokens);
    }

    private static int CountNonEmptyLines(SourceText text, Microsoft.CodeAnalysis.Text.TextSpan? span = null)
    {
        var startLine = span.HasValue ? text.Lines.GetLineFromPosition(span.Value.Start).LineNumber : 0;
        var endPosition = span.HasValue && span.Value.Length > 0 ? span.Value.End - 1 : span?.Start ?? text.Length;
        var endLine = span.HasValue ? text.Lines.GetLineFromPosition(endPosition).LineNumber : text.Lines.Count - 1;
        var count = 0;
        for (var index = startLine; index <= endLine; index++)
        {
            var line = text.Lines[index];
            if (!string.IsNullOrWhiteSpace(line.ToString())) count++;
        }
        return count;
    }

    private static void AddNamingIssue(
        ICollection<NamingIssue> issues,
        string kind,
        string name,
        Regex convention,
        string expected,
        Location location)
    {
        if (!convention.IsMatch(name))
            issues.Add(new NamingIssue(kind, name, expected, GetLocationLabel(location)));
    }

    private static string GetLocationLabel(Location? location)
    {
        if (location is null || !location.IsInSource) return "Metadata";
        var line = location.GetLineSpan().StartLinePosition;
        return $"{Path.GetFileName(location.SourceTree?.FilePath)}:{line.Line + 1}";
    }
}
