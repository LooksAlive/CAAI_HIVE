using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CAAI;

public enum ProjectSimilarityKind { TypeName, MemberName, MemberType, TypeShape, Usage }

public sealed record SimilaritySymbol(string Name, string DisplayName, string Kind, string FilePath, int Line, int Column);
public sealed record ProjectSimilarityMatch(ProjectSimilarityKind Kind, SimilaritySymbol Left, SimilaritySymbol Right,
    int Score, IReadOnlyList<string> Evidence);
public sealed record ProjectSimilarityReport(IReadOnlyList<ProjectSimilarityMatch> Matches, int SymbolCount,
    long ComparedPairs, IReadOnlyList<string> Warnings)
{
    public static ProjectSimilarityReport Empty { get; } = new([], 0, 0, []);
}

public sealed record ProjectSimilarityOptions
{
    public int MaxSymbols { get; init; } = 1500;
    public int MaxComparisons { get; init; } = 150000;
    public int MaxMatchesPerCategory { get; init; } = 100;
    public int MinimumPercent { get; init; } = 65;
}

public static class ProjectSimilarityAnalyzer
{
    private static readonly Regex NameWords = new(@"\p{Lu}+(?=\p{Lu}\p{Ll}|\p{Nd}|$)|\p{Lu}?\p{Ll}+|\p{L}+|\p{Nd}+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class Profile(ISymbol symbol, SimilaritySymbol item, bool isType)
    {
        public ISymbol Symbol { get; } = symbol;
        public SimilaritySymbol Item { get; } = item;
        public bool IsType { get; } = isType;
        public Dictionary<string, int> Types { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Shape { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> Usage { get; } = new(StringComparer.Ordinal);
    }

    public static ProjectSimilarityReport Analyze(Compilation compilation, ProjectSimilarityOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        options ??= new ProjectSimilarityOptions();
        if (options.MaxSymbols < 1 || options.MaxComparisons < 1 || options.MaxMatchesPerCategory < 1 || options.MinimumPercent is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(options));
        var profiles = new Dictionary<ISymbol, Profile>(SymbolEqualityComparer.Default);
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tree in compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = compilation.GetSemanticModel(tree);
            foreach (var declaration in tree.GetRoot(cancellationToken).DescendantNodes()
                .Where(node => node is BaseTypeDeclarationSyntax or DelegateDeclarationSyntax))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (model.GetDeclaredSymbol(declaration, cancellationToken) is not INamedTypeSymbol type) continue;
                var typeProfile = AddProfile(type, true);
                if (typeProfile == null) continue;
                foreach (var member in type.GetMembers().Where(member => !member.IsImplicitlyDeclared))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (member is not (IFieldSymbol or IPropertySymbol or IEventSymbol) &&
                        member is not IMethodSymbol { MethodKind: MethodKind.Ordinary }) continue;
                    var memberProfile = AddProfile(member, false);
                    if (memberProfile == null) continue;
                    if (memberProfile.Types.Count == 0) BuildMemberTypes(member, memberProfile.Types);
                }
            }
        }
        foreach (var profile in profiles.Values.Where(profile => profile.IsType))
        {
            var type = (INamedTypeSymbol)profile.Symbol;
            foreach (var member in type.GetMembers().Where(member => !member.IsImplicitlyDeclared))
            {
                if (member is not (IFieldSymbol or IPropertySymbol or IEventSymbol) && member is not IMethodSymbol { MethodKind: MethodKind.Ordinary }) continue;
                var signature = new Dictionary<string, int>(StringComparer.Ordinal);
                BuildMemberTypes(member, signature);
                var accessors = member is IPropertySymbol property ? $":get={property.GetMethod != null}:set={property.SetMethod != null}" : "";
                var arity = member is IMethodSymbol method ? $":arity={method.Arity}" : "";
                Add(profile.Shape, $"{member.Kind}:{(member.IsStatic ? "static" : "instance")}{arity}{accessors}:" +
                    string.Join(";", signature.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}*{pair.Value}")));
            }
            if (type.DelegateInvokeMethod is { } invoke) BuildMemberTypes(invoke, profile.Shape);
            if (type.BaseType is { SpecialType: SpecialType.None } baseType) Add(profile.Shape, $"base:{TypeKey(baseType)}");
            foreach (var implemented in type.Interfaces) Add(profile.Shape, $"interface:{TypeKey(implemented)}");
        }

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);
            foreach (var node in tree.GetRoot(cancellationToken).DescendantNodes())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax or InvocationExpressionSyntax or
                    ObjectCreationExpressionSyntax or ImplicitObjectCreationExpressionSyntax or ElementAccessExpressionSyntax)) continue;
                var operation = model.GetOperation(node, cancellationToken);
                var target = operation switch
                {
                    IInvocationOperation invocation => invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod,
                    IObjectCreationOperation creation => creation.Constructor,
                    IFieldReferenceOperation field => (ISymbol)field.Field,
                    IPropertyReferenceOperation property => property.Property,
                    IEventReferenceOperation eventReference => eventReference.Event,
                    _ => null
                };
                if (target == null || operation == null || operation.Syntax != node) continue;
                var owner = model.GetEnclosingSymbol(node.SpanStart, cancellationToken);
                while (owner != null && !profiles.ContainsKey(owner.OriginalDefinition)) owner = owner.ContainingSymbol;
                if (owner == null) continue;
                var ownerProfile = profiles[owner.OriginalDefinition];
                var write = operation.Parent is IAssignmentOperation assignment && ReferenceEquals(assignment.Target, operation);
                var usage = operation is IInvocationOperation or IObjectCreationOperation ? "calls" : write ? "writes" : "reads";
                var targetKey = target.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
                Add(ownerProfile.Usage, $"{usage}: {targetKey}");
                if (owner.ContainingType != null && profiles.TryGetValue(owner.ContainingType.OriginalDefinition, out var ownerType))
                    Add(ownerType.Usage, $"{usage}: {targetKey}");
                if (profiles.TryGetValue(target.OriginalDefinition, out var referenced))
                {
                    var context = $"used by: {owner.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)}";
                    Add(referenced.Usage, context);
                    if (target.ContainingType != null && profiles.TryGetValue(target.ContainingType.OriginalDefinition, out var targetType))
                        Add(targetType.Usage, context);
                }
            }
        }

        var queues = Enum.GetValues<ProjectSimilarityKind>().ToDictionary(kind => kind, _ => new PriorityQueue<ProjectSimilarityMatch, int>());
        var items = profiles.Values.OrderBy(profile => profile.Item.DisplayName, StringComparer.Ordinal)
            .ThenBy(profile => profile.Item.FilePath, StringComparer.Ordinal).ThenBy(profile => profile.Item.Line).ToArray();
        long comparisons = 0;
        var stopped = false;
        for (var leftIndex = 0; leftIndex < items.Length && !stopped; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < items.Length; rightIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var left = items[leftIndex];
                var right = items[rightIndex];
                if (left.IsType != right.IsType || left.Item.Kind != right.Item.Kind) continue;
                if (comparisons >= options.MaxComparisons)
                {
                    warnings.Add($"Comparison limit reached ({options.MaxComparisons:N0}); results are incomplete.");
                    stopped = true;
                    break;
                }
                comparisons++;
                Keep(left.IsType ? ProjectSimilarityKind.TypeName : ProjectSimilarityKind.MemberName, left, right,
                    NameSimilarity(left.Item.Name, right.Item.Name), [$"Names: {left.Item.Name} / {right.Item.Name}"]);
                if (left.IsType) Compare(ProjectSimilarityKind.TypeShape, left, right, left.Shape, right.Shape);
                else Compare(ProjectSimilarityKind.MemberType, left, right, left.Types, right.Types);
                Compare(ProjectSimilarityKind.Usage, left, right, left.Usage, right.Usage);
            }
        }
        return new ProjectSimilarityReport(queues.Values.SelectMany(queue => queue.UnorderedItems.Select(item => item.Element))
            .OrderBy(match => match.Kind).ThenByDescending(match => match.Score).ThenBy(match => match.Left.DisplayName, StringComparer.Ordinal)
            .ThenBy(match => match.Right.DisplayName, StringComparer.Ordinal).ToArray(), profiles.Count, comparisons, warnings.ToArray());

        Profile? AddProfile(ISymbol symbol, bool isType)
        {
            symbol = symbol.OriginalDefinition;
            if (profiles.TryGetValue(symbol, out var existing)) return existing;
            if (profiles.Count >= options.MaxSymbols) { warnings.Add($"Symbol limit reached ({options.MaxSymbols:N0}); results are incomplete."); return null; }
            var location = symbol.Locations.FirstOrDefault(location => location.IsInSource);
            if (location == null) return null;
            var span = location.GetLineSpan();
            var kind = symbol is INamedTypeSymbol named ? named.IsRecord ? "Record" : named.TypeKind.ToString() : symbol.Kind.ToString();
            var result = new Profile(symbol, new SimilaritySymbol(symbol.Name,
                symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), kind, span.Path,
                span.StartLinePosition.Line + 1, span.StartLinePosition.Character + 1), isType);
            profiles.Add(symbol, result);
            return result;
        }

        void Compare(ProjectSimilarityKind kind, Profile left, Profile right, Dictionary<string, int> first, Dictionary<string, int> second)
        {
            if (first.Count == 0 || second.Count == 0) return;
            var keys = first.Keys.Union(second.Keys).ToArray();
            var intersection = keys.Sum(key => Math.Min(first.GetValueOrDefault(key), second.GetValueOrDefault(key)));
            if (intersection == 0) return;
            var union = keys.Sum(key => Math.Max(first.GetValueOrDefault(key), second.GetValueOrDefault(key)));
            var evidence = first.Keys.Intersect(second.Keys).OrderBy(key => key, StringComparer.Ordinal).Take(8)
                .Select(key => $"{key} (shared count {Math.Min(first[key], second[key])})").ToArray();
            Keep(kind, left, right, (double)intersection / union, evidence);
        }

        void Keep(ProjectSimilarityKind kind, Profile left, Profile right, double similarity, IReadOnlyList<string> evidence)
        {
            var score = (int)Math.Round(similarity * 100, MidpointRounding.AwayFromZero);
            if (score < options.MinimumPercent) return;
            var queue = queues[kind];
            if (queue.Count >= options.MaxMatchesPerCategory)
            {
                warnings.Add($"At most {options.MaxMatchesPerCategory} highest-scoring matches per category are retained.");
                queue.TryPeek(out _, out var minimum);
                if (score <= minimum) return;
                queue.Dequeue();
            }
            queue.Enqueue(new ProjectSimilarityMatch(kind, left.Item, right.Item, score, evidence), score);
        }
    }

    public static double NameSimilarity(string left, string right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var firstWords = NameWords.Matches(left.TrimStart('_')).Select(match => match.Value.ToLowerInvariant()).ToArray();
        var secondWords = NameWords.Matches(right.TrimStart('_')).Select(match => match.Value.ToLowerInvariant()).ToArray();
        var first = string.Concat(firstWords);
        var second = string.Concat(secondWords);
        if (first.Length == 0 || second.Length == 0) return 0;
        if (first == second) return 1;
        var previous = Enumerable.Range(0, second.Length + 1).ToArray();
        var current = new int[second.Length + 1];
        for (var row = 1; row <= first.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= second.Length; column++)
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (first[row - 1] == second[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        var edit = 1d - (double)previous[second.Length] / Math.Max(first.Length, second.Length);
        var wordSet = firstWords.ToHashSet(StringComparer.Ordinal);
        var otherSet = secondWords.ToHashSet(StringComparer.Ordinal);
        var words = (double)wordSet.Intersect(otherSet).Count() / wordSet.Union(otherSet).Count();
        return Math.Max(edit, words);
    }

    private static void BuildMemberTypes(ISymbol symbol, Dictionary<string, int> features)
    {
        switch (symbol)
        {
            case IFieldSymbol field: Add(features, $"type: {TypeKey(field.Type)}"); break;
            case IPropertySymbol property:
                Add(features, $"type: {TypeKey(property.Type)}");
                foreach (var parameter in property.Parameters) Add(features, $"index {parameter.Ordinal}: {TypeKey(parameter.Type)}");
                break;
            case IEventSymbol eventSymbol: Add(features, $"type: {TypeKey(eventSymbol.Type)}"); break;
            case IMethodSymbol method:
                Add(features, $"return {method.RefKind}: {TypeKey(method.ReturnType)}");
                foreach (var parameter in method.Parameters) Add(features, $"parameter {parameter.Ordinal} {parameter.RefKind}: {TypeKey(parameter.Type)}");
                break;
        }
    }

    private static string TypeKey(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol parameter => $"{parameter.TypeParameterKind}:{parameter.Ordinal}",
        IArrayTypeSymbol array => $"{TypeKey(array.ElementType)}[{new string(',', array.Rank - 1)}]",
        INamedTypeSymbol { IsGenericType: true } named => named.OriginalDefinition.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) +
            $"<{string.Join(",", named.TypeArguments.Select(TypeKey))}>",
        _ => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
    };

    private static void Add(Dictionary<string, int> features, string feature) => features[feature] = features.GetValueOrDefault(feature) + 1;
}