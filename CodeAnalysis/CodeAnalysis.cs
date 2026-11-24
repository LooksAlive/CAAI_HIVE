using System.Collections;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace CAAI;

public sealed record InputAnalysisOptions
{
    public int MaxDepth { get; init; } = 8;
    public int MaxNodes { get; init; } = 2000;
    public int MaxCollectionItems { get; init; } = 100;
    public int MaxFlowPasses { get; init; } = 32;
    public bool EvaluateProperties { get; init; }
}

public sealed record InputPart(int Id, int? ParentId, string Path, string? MemberName,
    string TypeName, string? ScalarValue, bool IsCollectionItem);

public sealed record InputCodeConnection(int InputPartId, string Kind, string Symbol,
    string FilePath, int Line, int Column, int Start, int Length);

public sealed record InputAnalysisResult(IReadOnlyList<InputPart> Parts,
    IReadOnlyList<InputCodeConnection> Connections, IReadOnlyList<string> Warnings,
    bool FlowConverged)
{
    public void PrintToConsole()
    {
        Console.WriteLine("=== Input-to-code analysis (potential dependencies, not runtime coverage) ===");
        Console.WriteLine($"Parts: {Parts.Count}, connections: {Connections.Count}, flow converged: {FlowConverged}");
        Console.WriteLine("Input parts:");
        foreach (var part in Parts)
            Console.WriteLine($"  [{part.Id}] {part.Path} : {part.TypeName} | value: " +
                (part.ScalarValue == null ? "<null, not evaluated, or non-scalar>" :
                    System.Text.Json.JsonSerializer.Serialize(part.ScalarValue)));

        var partsById = Parts.ToDictionary(part => part.Id);
        Console.WriteLine("Connections:");
        foreach (var connection in Connections)
            Console.WriteLine($"  [{connection.InputPartId}] {partsById[connection.InputPartId].Path} -> " +
                $"{connection.Kind}: {connection.Symbol} at {connection.FilePath}:{connection.Line}:{connection.Column} " +
                $"(start: {connection.Start}, length: {connection.Length})");

        Console.WriteLine("Feature counts:");
        foreach (var feature in GetFeatureCounts())
            Console.WriteLine($"  {feature.Key} = {feature.Value}");
        Console.WriteLine("Warnings:");
        foreach (var warning in Warnings)
            Console.WriteLine($"  {warning}");
        Console.WriteLine("=== End input-to-code analysis ===");
    }

    public IReadOnlyDictionary<string, int> GetFeatureCounts() => Connections
        .GroupBy(connection => $"{connection.FilePath}:{connection.Start}:{connection.Kind}:{connection.Symbol}")
        .ToDictionary(group => group.Key, group => group.Select(connection => connection.InputPartId).Distinct().Count());

    public double SimilarityTo(InputAnalysisResult other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var left = GetFeatureCounts();
        var right = other.GetFeatureCounts();
        var keys = left.Keys.Union(right.Keys).ToArray();
        var total = keys.Sum(key => (double)Math.Max(left.GetValueOrDefault(key), right.GetValueOrDefault(key)));
        return total == 0 ? 0 : keys.Sum(key => (double)Math.Min(left.GetValueOrDefault(key), right.GetValueOrDefault(key))) / total;
    }
}

/// <summary>
/// Connects a bounded reflection snapshot to potential source dependencies, not runtime coverage.
/// Flow is conservative, flow-insensitive and context-insensitive; conditions are not executed.
/// </summary>
public sealed class CodeAnalysis
{
    private readonly Compilation _compilation;
    private readonly InputAnalysisOptions _options;

    public CodeAnalysis(Compilation compilation, InputAnalysisOptions? options = null)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
        _options = options ?? new InputAnalysisOptions();
        if (_options.MaxDepth < 0 || _options.MaxNodes < 1 ||
            _options.MaxCollectionItems < 1 || _options.MaxFlowPasses < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public InputAnalysisResult AnalyzeInspector(ObjectNode node, IParameterSymbol? parameter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (node.IsDeferred || node.IsError || node.IsGroup || node.RawValue == null)
            throw new ArgumentException("Select an evaluated inspector value with local runtime data.", nameof(node));
        return Analyze(node.RawValue, node.Name, parameter, cancellationToken);
    }

    public InputAnalysisResult AnalyzeEnvironment(EnvironmentEntity entity, IParameterSymbol? parameter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.Type != EntityType.DataValue)
            throw new ArgumentException("Select a data value from the environment.", nameof(entity));
        return Analyze(entity.Value, entity.Name, parameter, cancellationToken);
    }

    public InputAnalysisResult Analyze(object? input, string name = "input", IParameterSymbol? parameter = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var parts = new List<InputPart>();
        var types = new Dictionary<int, Type>();
        var warnings = new HashSet<string>(StringComparer.Ordinal);
        var ancestors = new HashSet<object>(System.Collections.Generic.ReferenceEqualityComparer.Instance);
        Walk(input, input?.GetType(), null, name, null, false, 0);
        var children = parts.Where(part => part.ParentId.HasValue)
            .ToLookup(part => part.ParentId!.Value);
        var operations = new List<IOperation>();
        foreach (var tree in _compilation.SyntaxTrees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var model = _compilation.GetSemanticModel(tree);
            foreach (var syntax in tree.GetRoot(cancellationToken).DescendantNodesAndSelf())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (syntax is not (BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or
                    LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax or
                    GlobalStatementSyntax or EqualsValueClauseSyntax)) continue;
                if (model.GetOperation(syntax, cancellationToken) is { Parent: null } operation)
                    operations.Add(operation);
            }
        }

        var flows = new Dictionary<ISymbol, HashSet<int>>(SymbolEqualityComparer.Default);
        var returns = new Dictionary<ISymbol, HashSet<int>>(SymbolEqualityComparer.Default);
        var links = new HashSet<InputCodeConnection>();
        var changed = false;
        var bindings = operations.SelectMany(Flatten).Select(operation => operation.SemanticModel?
                .GetEnclosingSymbol(operation.Syntax.SpanStart, cancellationToken))
            .OfType<IMethodSymbol>().SelectMany(method => method.Parameters)
            .Distinct<IParameterSymbol>(SymbolEqualityComparer.Default).ToArray();
        if (parameter != null)
        {
            var binding = bindings.FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate, parameter));
            if (binding == null)
                throw new ArgumentException("The parameter must have a body in this compilation.", nameof(parameter));
            Merge(flows, binding, [0]);
        }
        else if (input != null)
        {
            foreach (var binding in bindings.Where(binding => Matches(input.GetType(), binding.Type)))
                Merge(flows, binding, [0]);
            warnings.Add("Entry parameters were inferred by type; supply a parameter to restrict the analysis to one entry point.");
        }
        if (flows.Count == 0) warnings.Add("No entry parameter matched the input. Supply an explicit source parameter binding.");
        warnings.Add("Connections are potential dependencies, not evidence that a branch executed. Calls share parameter flow across call sites; external method results conservatively inherit argument flow.");

        var converged = false;
        for (var pass = 0; pass < _options.MaxFlowPasses; pass++)
        {
            changed = false;
            foreach (var operation in operations) Evaluate(operation);
            if (!changed) { converged = true; break; }
        }
        if (!converged) warnings.Add("Flow pass limit reached; connections may be incomplete.");
        var result = new InputAnalysisResult(parts.ToArray(), links.OrderBy(link => link.FilePath)
            .ThenBy(link => link.Start).ThenBy(link => link.InputPartId).ToArray(), warnings.ToArray(), converged);
        result.PrintToConsole();
        return result;

        void Walk(object? value, Type? declaredType, int? parent, string path, string? member, bool item, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (parts.Count >= _options.MaxNodes) { warnings.Add("Input node limit reached."); return; }
            var type = value?.GetType() ?? declaredType;
            var scalar = type == null || IsScalar(type);
            var id = parts.Count;
            parts.Add(new InputPart(id, parent, path, member, type?.FullName ?? "null",
                scalar ? FormatScalar(value) : null, item));
            if (type != null) types[id] = type;
            if (value == null || scalar) return;
            if (depth >= _options.MaxDepth) { warnings.Add($"Depth limit at {path}."); return; }
            if (!type!.IsValueType && !ancestors.Add(value)) { warnings.Add($"Cycle at {path}."); return; }
            try
            {
                if (value is IEnumerable enumerable)
                {
                    var enumerator = enumerable.GetEnumerator();
                    try
                    {
                        var index = 0;
                        while (index < _options.MaxCollectionItems && parts.Count < _options.MaxNodes && enumerator.MoveNext())
                        {
                            Walk(enumerator.Current, null, id, $"{path}[{index}]", null, true, depth + 1);
                            index++;
                        }
                        if (index == _options.MaxCollectionItems || parts.Count >= _options.MaxNodes)
                            warnings.Add($"Collection limit at {path}; more items may exist.");
                    }
                    finally { (enumerator as IDisposable)?.Dispose(); }
                    return;
                }
                foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public).OrderBy(field => field.Name))
                {
                    if (parts.Count >= _options.MaxNodes) { warnings.Add("Input node limit reached."); break; }
                    try { Walk(field.GetValue(value), field.FieldType, id, $"{path}.{field.Name}", field.Name, false, depth + 1); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { warnings.Add($"Cannot read {path}.{field.Name}: {exception.GetBaseException().Message}"); }
                }
                foreach (var property in type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                    .Where(property => property.GetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0)
                    .OrderBy(property => property.Name))
                {
                    if (parts.Count >= _options.MaxNodes) { warnings.Add("Input node limit reached."); break; }
                    if (!_options.EvaluateProperties)
                    {
                        Walk(null, property.PropertyType, id, $"{path}.{property.Name}", property.Name, false, depth + 1);
                        warnings.Add("Property getters were not evaluated; property values and nested property data are unavailable.");
                        continue;
                    }
                    try { Walk(property.GetValue(value), property.PropertyType, id, $"{path}.{property.Name}", property.Name, false, depth + 1); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception) { warnings.Add($"Cannot read {path}.{property.Name}: {exception.GetBaseException().Message}"); }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { warnings.Add($"Cannot walk {path}: {exception.GetBaseException().Message}"); }
            finally { if (!type.IsValueType) ancestors.Remove(value); }
        }

        HashSet<int> Evaluate(IOperation operation)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var childValues = operation.ChildOperations.Select(child => (Operation: child, Values: Evaluate(child))).ToArray();
            var values = childValues.SelectMany(child => child.Values).ToHashSet();
            HashSet<int> Value(IOperation? child) => child == null ? [] :
                childValues.FirstOrDefault(pair => ReferenceEquals(pair.Operation, child)).Values ?? [];
            switch (operation)
            {
                case IParameterReferenceOperation reference:
                    values = Read(flows, reference.Parameter);
                    break;
                case ILocalReferenceOperation reference:
                    values = Read(flows, reference.Local);
                    break;
                case IFieldReferenceOperation field:
                    values = Members(Value(field.Instance), field.Field.Name);
                    values.UnionWith(Read(flows, field.Field));
                    break;
                case IPropertyReferenceOperation property:
                    values = property.Property.IsIndexer
                        ? Items(Value(property.Instance), property.Arguments.FirstOrDefault()?.Value.ConstantValue)
                        : Members(Value(property.Instance), property.Property.Name);
                    values.UnionWith(Read(flows, property.Property));
                    break;
                case IArrayElementReferenceOperation element:
                    values = Items(Value(element.ArrayReference), element.Indices.Length == 1 ? element.Indices[0].ConstantValue : null);
                    break;
                case IVariableDeclaratorOperation variable:
                    Merge(flows, variable.Symbol, values);
                    break;
                case ISimpleAssignmentOperation assignment:
                    values = Value(assignment.Value);
                    if (ReferencedSymbol(assignment.Target) is { } target) Merge(flows, target, values);
                    break;
                case IForEachLoopOperation loop:
                    var items = Items(Value(loop.Collection), null);
                    foreach (var local in loop.Locals) Merge(flows, local, items);
                    break;
                case IInvocationOperation invocation:
                    Connect(operation, values, "Call");
                    foreach (var argument in invocation.Arguments)
                        if (argument.Parameter != null)
                            Merge(flows, argument.Parameter.OriginalDefinition, Value(argument));
                    var returned = Read(returns, invocation.TargetMethod.OriginalDefinition);
                    if (invocation.TargetMethod.DeclaringSyntaxReferences.Length > 0) values = returned;
                    else values.UnionWith(returned);
                    break;
                case IReturnOperation:
                    if (operation.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart, cancellationToken) is IMethodSymbol method)
                        Merge(returns, method.OriginalDefinition, values);
                    break;
                case IMethodBodyOperation body when body.ExpressionBody != null:
                    if (operation.SemanticModel?.GetEnclosingSymbol(operation.Syntax.SpanStart, cancellationToken) is IMethodSymbol expressionMethod)
                        Merge(returns, expressionMethod.OriginalDefinition, Value(body.ExpressionBody));
                    break;
            }
            var kind = operation switch
            {
                IFieldReferenceOperation or IPropertyReferenceOperation => "Member",
                IParameterReferenceOperation or ILocalReferenceOperation => "Reference",
                IReturnOperation => "Return",
                _ => null
            };
            if (kind != null) Connect(operation, values, kind);
            if (operation is IConditionalOperation conditional) Connect(conditional.Condition, Value(conditional.Condition), "Condition");
            if (operation is IWhileLoopOperation { Condition: { } condition }) Connect(condition, Value(condition), "Condition");
            if (operation is IForLoopOperation { Condition: { } forCondition }) Connect(forCondition, Value(forCondition), "Condition");
            if (operation is ISwitchOperation selection) Connect(selection.Value, Value(selection.Value), "Condition");
            if (operation is ISwitchExpressionOperation expression) Connect(expression.Value, Value(expression.Value), "Condition");
            return values;
        }

        HashSet<int> Members(IEnumerable<int> parents, string member)
        {
            var result = new HashSet<int>();
            foreach (var parent in parents)
            {
                var matching = children[parent].Where(part => part.MemberName == member).Select(part => part.Id).ToArray();
                if (matching.Length > 0) result.UnionWith(matching);
                else result.Add(parent);
            }
            return result;
        }

        HashSet<int> Items(IEnumerable<int> parents, Optional<object?>? index)
        {
            var result = new HashSet<int>();
            foreach (var parent in parents)
            {
                var items = children[parent].Where(part => part.IsCollectionItem).ToArray();
                if (index is { HasValue: true, Value: int offset } && types.GetValueOrDefault(parent) is { } collectionType &&
                    (collectionType.IsArray || typeof(IList).IsAssignableFrom(collectionType)))
                    result.UnionWith(items.Where(part => part.Path == $"{parts[parent].Path}[{offset}]").Select(part => part.Id));
                else result.UnionWith(items.Select(part => part.Id));
                if (items.Length == 0) result.Add(parent);
            }
            return result;
        }

        void Merge(Dictionary<ISymbol, HashSet<int>> map, ISymbol symbol, IEnumerable<int> values)
        {
            symbol = symbol.OriginalDefinition;
            if (!map.TryGetValue(symbol, out var existing)) map[symbol] = existing = [];
            foreach (var value in values) if (existing.Add(value)) changed = true;
        }

        void Connect(IOperation operation, IEnumerable<int> values, string kind)
        {
            var location = operation.Syntax.GetLocation().GetLineSpan();
            var symbol = (ReferencedSymbol(operation) ?? operation.SemanticModel?
                .GetEnclosingSymbol(operation.Syntax.SpanStart, cancellationToken))?.ToDisplayString() ?? "";
            foreach (var value in values)
                links.Add(new InputCodeConnection(value, kind, symbol, location.Path,
                    location.StartLinePosition.Line + 1, location.StartLinePosition.Character + 1,
                    operation.Syntax.SpanStart, operation.Syntax.Span.Length));
        }
    }

    private static HashSet<int> Read(Dictionary<ISymbol, HashSet<int>> map, ISymbol symbol) =>
        map.TryGetValue(symbol.OriginalDefinition, out var values) ? new HashSet<int>(values) : [];

    private static ISymbol? ReferencedSymbol(IOperation operation) => operation switch
    {
        IParameterReferenceOperation reference => reference.Parameter,
        ILocalReferenceOperation reference => reference.Local,
        IFieldReferenceOperation reference => reference.Field,
        IPropertyReferenceOperation reference => reference.Property,
        IInvocationOperation invocation => invocation.TargetMethod,
        _ => null
    };

    private static IEnumerable<IOperation> Flatten(IOperation operation)
    {
        yield return operation;
        foreach (var child in operation.ChildOperations)
            foreach (var descendant in Flatten(child)) yield return descendant;
    }

    private static bool Matches(Type runtimeType, ITypeSymbol symbol)
    {
        if (symbol is IArrayTypeSymbol array)
            return runtimeType.IsArray && runtimeType.GetArrayRank() == array.Rank && Matches(runtimeType.GetElementType()!, array.ElementType);
        if (symbol is not INamedTypeSymbol named) return false;
        for (var type = runtimeType; type != null; type = type.BaseType)
            if (Exact(type, named)) return true;
        return runtimeType.GetInterfaces().Any(type => Exact(type, named));
    }

    private static bool Exact(Type type, INamedTypeSymbol symbol)
    {
        var definition = type.IsGenericType ? type.GetGenericTypeDefinition() : type;
        var names = new Stack<string>();
        for (var current = symbol; current != null; current = current.ContainingType) names.Push(current.MetadataName);
        var prefix = symbol.ContainingNamespace.IsGlobalNamespace ? "" : symbol.ContainingNamespace.ToDisplayString() + ".";
        if (definition.FullName != prefix + string.Join("+", names)) return false;
        var arguments = type.IsGenericType ? type.GetGenericArguments() : Type.EmptyTypes;
        return arguments.Length == symbol.TypeArguments.Length && arguments.Zip(symbol.TypeArguments).All(pair => Matches(pair.First, pair.Second));
    }

    private static bool IsScalar(Type type) => type.IsPrimitive || type.IsEnum || type == typeof(string) ||
        type == typeof(decimal) || type == typeof(DateTime) || type == typeof(DateTimeOffset) ||
        type == typeof(TimeSpan) || type == typeof(Guid) || type == typeof(Uri);

    private static string? FormatScalar(object? value) => value switch
    {
        null => null,
        string text => text.Length <= 200 ? text : text[..200],
        Uri uri => uri.OriginalString,
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)
    };
}