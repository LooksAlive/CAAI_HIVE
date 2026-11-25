using Microsoft.CodeAnalysis;

namespace Gsi.Analysis;

// ---------------------------------------------------------------------------
// Data shapes — the structural signature of an input sample
// ---------------------------------------------------------------------------

public sealed record DataShape(
    string SampleId,
    string FilePath,
    long ByteSize,
    string Format,                    // "text", "json", "xml", "pdf", "binary", "unknown"
    IReadOnlyList<string> TokenTypes, // for text: token-type sequence
    IReadOnlyList<TypeSignature> NestedTypes, // for structured data
    IReadOnlyDictionary<string, int> FieldFrequency,
    IReadOnlyList<string> SampleValues);

public sealed record TypeSignature(
    string Name,                      // "object", "array", "string", "number", "user"
    IReadOnlyList<TypeSignature> Children);

// ---------------------------------------------------------------------------
// Type graph — hierarchy + data flow
// ---------------------------------------------------------------------------

public enum TypeRelation
{
    Inherits,
    Implements,
    Contains,       // field/property
    ParameterOf,    // parameter type
    Returns,        // return type
    Produces,       // method creates this type (return value declared)
    Consumes,       // method takes this type
    Transforms,     // method takes A, returns B
}

public sealed record TypeEdge(
    string FromId, string ToId,
    TypeRelation Relation,
    string? ViaMember);               // field or method name

public sealed record TypeNode(
    string Id, string Name, string Kind,
    string? Namespace,
    int Depth,                        // inheritance depth
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> Children,
    IReadOnlyList<string> Fields);

// ---------------------------------------------------------------------------
// Coverage — how well the type graph explains a data shape
// ---------------------------------------------------------------------------

public enum CoverageKind
{
    Full,         // data shape exactly matches an existing type
    Partial,      // matches some fields but not all
    Structural,   // matches by structure (nesting, arity) but not by name/type
    None,         // no match
}

public sealed record Coverage(
    string SampleId,
    CoverageKind Kind,
    double Score,                     // 0..1
    string? MatchedTypeId,
    string? MatchedTypeName,
    IReadOnlyList<string> MatchedParts,   // which fields/types matched
    IReadOnlyList<string> MissingParts,   // which fields/types are unmatched
    IReadOnlyList<string> SuggestedFields // suggested new fields from data
);

// ---------------------------------------------------------------------------
// Patterns — emergent structure discovered by combining the above
// ---------------------------------------------------------------------------

public enum PatternKind
{
    TypeSimilarity,           // two types share fields
    MethodSimilarity,         // two methods share structure
    DataTypeMatch,            // data shape maps to type
    DataTypeGap,              // data shape doesn't map to any type
    FieldCluster,             // fields co-occur across types
    NamingCluster,            // names share prefix/suffix across types
    UsageCluster,             // types used together
    HierarchicalGap,          // type exists but no data maps to it
    DataOrphan,               // data exists but no type maps to it
}

public sealed record Pattern(
    PatternKind Kind,
    string Id,
    string Description,
    double Score,
    IReadOnlyList<string> Members,        // entity ids or sample ids
    IReadOnlyDictionary<string, object?> Evidence);

// ---------------------------------------------------------------------------
// The full report
// ---------------------------------------------------------------------------

public sealed record DataAnalysisReport(
    IReadOnlyList<DataShape> DataShapes,
    IReadOnlyList<TypeNode> TypeNodes,
    IReadOnlyList<TypeEdge> TypeEdges,
    IReadOnlyList<Coverage> Coverage,
    IReadOnlyList<Pattern> Patterns,
    IReadOnlyList<string> Warnings);

public sealed class DataShapeAnalyzer
{
    public async Task<List<DataShape>> AnalyzeAsync(
        IEnumerable<(string SampleId, string FilePath)> samples,
        CancellationToken ct = default)
    {
        var result = new List<DataShape>();

        foreach (var (id, path) in samples)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var bytes = await File.ReadAllBytesAsync(path, ct);
                var format = DetectFormat(bytes, path);
                var shape = format switch
                {
                    "text" => AnalyzeText(bytes),
                    "json" => AnalyzeJson(bytes),
                    "xml"  => AnalyzeXml(bytes),
                    "binary" => AnalyzeBinary(bytes),
                    _ => AnalyzeBinary(bytes),
                };
                result.Add(new DataShape(id, path, bytes.LongLength, format,
                    shape.Tokens, shape.Nested, shape.Fields, shape.Samples));
            }
            catch (Exception ex)
            {
                result.Add(new DataShape(id, path, 0, "error",
                    Array.Empty<string>(), Array.Empty<TypeSignature>(),
                    new Dictionary<string, int>(),
                    new[] { ex.Message }));
            }
        }
        return result;
    }

    private static string DetectFormat(byte[] bytes, string path)
    {
        if (bytes.Length == 0) return "binary";

        // JSON
        var start = System.Text.Encoding.UTF8.GetString(bytes, 0, Math.Min(256, bytes.Length)).TrimStart();
        if (start.StartsWith("{") || start.StartsWith("[")) return "json";
        if (start.StartsWith("<")) return "xml";

        // PDF
        if (bytes.Length >= 5 &&
            bytes[0] == '%' && bytes[1] == 'P' && bytes[2] == 'D' && bytes[3] == 'F' && bytes[4] == '-')
            return "pdf";

        // Heuristic text detection: > 90% printable ASCII/UTF8
        int printable = 0;
        int checkLen = Math.Min(bytes.Length, 4096);
        for (int i = 0; i < checkLen; i++)
        {
            var b = bytes[i];
            if ((b >= 32 && b < 127) || b == 9 || b == 10 || b == 13) printable++;
        }
        if (printable * 100 / checkLen > 90) return "text";

        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".json" => "json", ".xml" => "xml", ".pdf" => "pdf",
            ".txt" or ".md" or ".html" or ".htm" => "text",
            _ => "binary",
        };
    }

    private static ShapeParts AnalyzeText(byte[] bytes)
    {
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        var tokens = new List<string>();
        var sb = new System.Text.StringBuilder();

        foreach (var ch in text)
        {
            string? kind = null;
            if (char.IsLetter(ch)) kind = "L";
            else if (char.IsDigit(ch)) kind = "D";
            else if (char.IsWhiteSpace(ch)) kind = "W";
            else if (char.IsPunctuation(ch)) kind = "P";
            else if (char.IsSymbol(ch)) kind = "S";

            if (kind is null) continue;
            if (sb.Length == 0 || sb[^1] == kind[0]) { sb.Append(kind[0]); }
            else { tokens.Add(sb.ToString()); sb.Clear(); sb.Append(kind[0]); }
        }
        if (sb.Length > 0) tokens.Add(sb.ToString());

        // Keep a compact signature: group runs, collapse repeated patterns.
        var signature = tokens
            .GroupBy(t => t[0])
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        return new ShapeParts(
            tokens.Take(200).ToList(),
            Array.Empty<TypeSignature>(),
            signature,
            new[] { text.Substring(0, Math.Min(80, text.Length)) });
    }

    private static ShapeParts AnalyzeJson(byte[] bytes)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            var fields = new Dictionary<string, int>();
            var nested = new List<TypeSignature>();
            WalkJson(doc.RootElement, fields, nested, depth: 0);
            return new ShapeParts(
                Array.Empty<string>(),
                nested,
                fields,
                new[] { text.Substring(0, Math.Min(80, text.Length)) });
        }
        catch
        {
            return new ShapeParts(Array.Empty<string>(), Array.Empty<TypeSignature>(),
                new Dictionary<string, int>(), Array.Empty<string>());
        }
    }

    private static void WalkJson(System.Text.Json.JsonElement el,
        Dictionary<string, int> fields, List<TypeSignature> nested, int depth)
    {
        if (depth > 6) return;
        switch (el.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    fields[prop.Name] = fields.GetValueOrDefault(prop.Name) + 1;
                    var children = new List<TypeSignature>();
                    WalkJson(prop.Value, fields, children, depth + 1);
                    nested.Add(new TypeSignature($"prop:{prop.Name}", children));
                }
                break;
            case System.Text.Json.JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    WalkJson(item, fields, nested, depth + 1);
                break;
            case System.Text.Json.JsonValueKind.String:
                nested.Add(new TypeSignature("string", Array.Empty<TypeSignature>()));
                break;
            case System.Text.Json.JsonValueKind.Number:
                nested.Add(new TypeSignature("number", Array.Empty<TypeSignature>()));
                break;
            case System.Text.Json.JsonValueKind.True:
            case System.Text.Json.JsonValueKind.False:
                nested.Add(new TypeSignature("bool", Array.Empty<TypeSignature>()));
                break;
            case System.Text.Json.JsonValueKind.Null:
                nested.Add(new TypeSignature("null", Array.Empty<TypeSignature>()));
                break;
        }
    }

    private static ShapeParts AnalyzeXml(byte[] bytes)
    {
        try
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            using var reader = System.Xml.XmlReader.Create(new StringReader(text));
            var fields = new Dictionary<string, int>();
            var nested = new List<TypeSignature>();
            while (reader.Read())
            {
                if (reader.NodeType == System.Xml.XmlNodeType.Element)
                    fields[reader.Name] = fields.GetValueOrDefault(reader.Name) + 1;
            }
            return new ShapeParts(Array.Empty<string>(), nested, fields,
                new[] { text.Substring(0, Math.Min(80, text.Length)) });
        }
        catch
        {
            return new ShapeParts(Array.Empty<string>(), Array.Empty<TypeSignature>(),
                new Dictionary<string, int>(), Array.Empty<string>());
        }
    }

    private static ShapeParts AnalyzeBinary(byte[] bytes)
    {
        var fields = new Dictionary<string, int> { ["length"] = bytes.Length };
        var sig = new List<string>();
        for (int i = 0; i < Math.Min(bytes.Length, 64); i++)
            sig.Add(bytes[i].ToString("X2"));
        return new ShapeParts(sig, Array.Empty<TypeSignature>(), fields, Array.Empty<string>());
    }

    private sealed record ShapeParts(
        IReadOnlyList<string> Tokens,
        IReadOnlyList<TypeSignature> Nested,
        IReadOnlyDictionary<string, int> Fields,
        IReadOnlyList<string> Samples);
}

public sealed class TypeFlowAnalyzer
{
    private readonly Workspace _workspace;
    public TypeFlowAnalyzer(Workspace workspace) => _workspace = workspace;

    public async Task<(List<TypeNode> Nodes, List<TypeEdge> Edges)>
        AnalyzeAsync(CancellationToken ct = default)
    {
        var nodes = new List<TypeNode>();
        var edges = new List<TypeEdge>();

        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            ct.ThrowIfCancellationRequested();
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null) continue;

            foreach (var symbol in GetAllTypes(compilation.Assembly.GlobalNamespace))
            {
                ct.ThrowIfCancellationRequested();

                var id = SymbolId(symbol);
                var parents = new List<string>();
                if (symbol.BaseType is { SpecialType: SpecialType.None })
                    parents.Add(SymbolId(symbol.BaseType));
                parents.AddRange(symbol.Interfaces.Select(SymbolId));

                var fields = symbol.GetMembers()
                    .OfType<IFieldSymbol>()
                    .Where(f => !f.IsImplicitlyDeclared)
                    .Select(f => $"{f.Name}:{f.Type.ToDisplayString()}")
                    .ToList();

                nodes.Add(new TypeNode(
                    Id: id,
                    Name: symbol.Name,
                    Kind: symbol.TypeKind.ToString(),
                    Namespace: symbol.ContainingNamespace?.ToDisplayString(),
                    Depth: ComputeDepth(symbol),
                    Parents: parents,
                    Children: new List<string>(),
                    Fields: fields));

                // Inheritance edges
                foreach (var p in parents)
                    edges.Add(new TypeEdge(id, p, TypeRelation.Inherits, null));

                // Field containment
                foreach (var f in symbol.GetMembers().OfType<IFieldSymbol>())
                    if (!f.IsImplicitlyDeclared && f.Type is INamedTypeSymbol ft)
                        edges.Add(new TypeEdge(id, SymbolId(ft), TypeRelation.Contains, f.Name));

                // Method production and consumption
                foreach (var m in symbol.GetMembers().OfType<IMethodSymbol>())
                {
                    if (m.ReturnType is INamedTypeSymbol rt && rt.SpecialType == SpecialType.None)
                        edges.Add(new TypeEdge(id, SymbolId(rt), TypeRelation.Returns, m.Name));
                    foreach (var p in m.Parameters)
                        if (p.Type is INamedTypeSymbol pt && pt.SpecialType == SpecialType.None)
                            edges.Add(new TypeEdge(id, SymbolId(pt), TypeRelation.ParameterOf, m.Name));
                }
            }
        }

        // Fill in children based on parents.
        var nodeById = nodes.ToDictionary(n => n.Id, n => n, StringComparer.Ordinal);
        foreach (var n in nodes)
            foreach (var p in n.Parents)
                if (nodeById.TryGetValue(p, out var parentNode))
                    ((List<string>)parentNode.Children).Add(n.Id);

        return (nodes, edges);
    }

    private static IEnumerable<INamedTypeSymbol> GetAllTypes(INamespaceSymbol ns)
    {
        foreach (var t in ns.GetTypeMembers())
            yield return t;
        foreach (var child in ns.GetNamespaceMembers())
            foreach (var t in GetAllTypes(child))
                yield return t;
    }

    private static int ComputeDepth(INamedTypeSymbol t)
    {
        int d = 0;
        var cur = t.BaseType;
        while (cur is not null && cur.SpecialType != SpecialType.System_Object)
        {
            d++;
            cur = cur.BaseType;
        }
        return d;
    }

    private static string SymbolId(ISymbol s)
        => s.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
}

public sealed class CoverageAnalyzer
{
    public List<Coverage> Analyze(
        IReadOnlyList<DataShape> dataShapes,
        IReadOnlyList<TypeNode> typeNodes)
    {
        var result = new List<Coverage>();
        var typesByName = typeNodes.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var shape in dataShapes)
        {
            var best = FindBestMatch(shape, typeNodes);
            result.Add(best);
        }
        return result;
    }

    private static Coverage FindBestMatch(DataShape shape, IReadOnlyList<TypeNode> types)
    {
        // Score every type against the data shape.
        var scored = new List<(TypeNode Type, double Score, List<string> Matched, List<string> Missing)>();

        foreach (var type in types)
        {
            var (score, matched, missing) = ScoreShape(shape, type);
            if (score > 0)
                scored.Add((type, score, matched, missing));
        }

        if (scored.Count == 0)
        {
            // No type scored — this data shape is orphaned.
            return new Coverage(shape.SampleId, CoverageKind.None, 0,
                null, null,
                Array.Empty<string>(),
                Array.Empty<string>(),
                shape.FieldFrequency.Keys.ToList());
        }

        var best = scored.OrderByDescending(s => s.Score).First();
        var kind = best.Score switch
        {
            >= 0.95 => CoverageKind.Full,
            >= 0.6  => CoverageKind.Partial,
            >= 0.3  => CoverageKind.Structural,
            _        => CoverageKind.None,
        };

        var suggested = shape.FieldFrequency.Keys
            .Where(f => !best.Type.Fields.Any(tf => tf.StartsWith(f + ":", StringComparison.OrdinalIgnoreCase)))
            .ToList();

        return new Coverage(shape.SampleId, kind, best.Score,
            best.Type.Id, best.Type.Name,
            best.Matched, best.Missing, suggested);
    }

    private static (double Score, List<string> Matched, List<string> Missing)
        ScoreShape(DataShape shape, TypeNode type)
    {
        var matched = new List<string>();
        var missing = new List<string>();

        // For text: does the type have a "text-ish" field?
        if (shape.Format == "text")
        {
            var hasTextField = type.Fields.Any(f =>
                f.Contains("string", StringComparison.OrdinalIgnoreCase) ||
                f.Contains("Text", StringComparison.OrdinalIgnoreCase));
            return hasTextField ? (0.5, new List<string> { "text-like field" }, new List<string>()) : (0, new(), new());
        }

        // For structured: do field names overlap?
        var typeFields = type.Fields
            .Select(f => f.Split(':')[0])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dataFields = shape.FieldFrequency.Keys.ToList();

        if (typeFields.Count == 0 || dataFields.Count == 0)
            return (0, new(), dataFields);

        foreach (var df in dataFields)
            if (typeFields.Contains(df)) matched.Add(df);
            else missing.Add(df);

        var score = (double)matched.Count / Math.Max(typeFields.Count, dataFields.Count);
        return (score, matched, missing);
    }
}

public sealed class PatternSynthesizer
{
    public List<Pattern> Synthesize(
        IReadOnlyList<DataShape> shapes,
        IReadOnlyList<TypeNode> types,
        IReadOnlyList<TypeEdge> edges,
        IReadOnlyList<Coverage> coverage)
    {
        var patterns = new List<Pattern>();

        // 1. Data-orphan: data exists but no type matches
        foreach (var cov in coverage.Where(c => c.Kind == CoverageKind.None))
        {
            patterns.Add(new Pattern(
                PatternKind.DataOrphan,
                $"orphan-{cov.SampleId}",
                $"Data shape from {cov.SampleId} doesn't match any type. Missing fields: {string.Join(", ", cov.SuggestedFields)}",
                Score: 0.9,
                Members: new[] { cov.SampleId },
                Evidence: new Dictionary<string, object?>
                {
                    ["format"] = shapes.First(s => s.SampleId == cov.SampleId).Format,
                    ["fields"] = cov.SuggestedFields,
                }));
        }

        // 2. Type-orphan: type exists but no data shape matches
        var matchedTypeIds = coverage
            .Where(c => c.MatchedTypeId != null)
            .Select(c => c.MatchedTypeId!)
            .ToHashSet();
        foreach (var type in types.Where(t => !matchedTypeIds.Contains(t.Id)))
        {
            patterns.Add(new Pattern(
                PatternKind.HierarchicalGap,
                $"unused-{type.Id}",
                $"Type {type.Name} has no matching data sample. Consider adding samples or removing the type.",
                Score: 0.7,
                Members: new[] { type.Id },
                Evidence: new Dictionary<string, object?> { ["kind"] = type.Kind }));
        }

        // 3. Partial coverage: data partially maps to a type (a node kind)
        foreach (var cov in coverage.Where(c => c.Kind == CoverageKind.Partial))
        {
            patterns.Add(new Pattern(
                PatternKind.DataTypeGap,
                $"partial-{cov.SampleId}-{cov.MatchedTypeId}",
                $"Data shape {cov.SampleId} partially matches {cov.MatchedTypeName}. Add fields: {string.Join(", ", cov.SuggestedFields)}",
                Score: cov.Score,
                Members: new[] { cov.SampleId, cov.MatchedTypeId! },
                Evidence: new Dictionary<string, object?>
                {
                    ["missing"] = cov.SuggestedFields,
                    ["matched"] = cov.MatchedParts,
                }));
        }

        // 4. Structural motifs from shared fields (reuse from previous report)
        var fieldClusters = types
            .SelectMany(t => t.Fields.Select(f => (Field: f.Split(':')[0], Type: t)))
            .GroupBy(x => x.Field)
            .Where(g => g.Count() >= 3)
            .Select(g => new Pattern(
                PatternKind.FieldCluster,
                $"field-{g.Key}",
                $"Field '{g.Key}' appears in {g.Count()} types — potential shared shape.",
                Score: Math.Min(1.0, g.Count() / 10.0),
                Members: g.Select(x => x.Type.Id).ToList(),
                Evidence: new Dictionary<string, object?> { ["count"] = g.Count() }));

        patterns.AddRange(fieldClusters);

        return patterns.OrderByDescending(p => p.Score).ToList();
    }
}









public sealed class DataCodeAnalyzer
{
    private readonly Workspace _workspace;
    public DataCodeAnalyzer(Workspace workspace) => _workspace = workspace;

    public async Task<DataAnalysisReport> AnalyzeAsync(
        IEnumerable<(string SampleId, string FilePath)> samples,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // 1. Input data shapes
        var shapeAnalyzer = new DataShapeAnalyzer();
        var shapes = await shapeAnalyzer.AnalyzeAsync(samples, ct);

        // 2. Type hierarchy and data flow
        var flowAnalyzer = new TypeFlowAnalyzer(_workspace);
        var (nodes, edges) = await flowAnalyzer.AnalyzeAsync(ct);

        // 3. Coverage
        var coverageAnalyzer = new CoverageAnalyzer();
        var coverage = coverageAnalyzer.Analyze(shapes, nodes);

        // 4. Patterns
        var synthesizer = new PatternSynthesizer();
        var patterns = synthesizer.Synthesize(shapes, nodes, edges, coverage);

        return new DataAnalysisReport(shapes, nodes, edges, coverage, patterns, warnings);
    }
}