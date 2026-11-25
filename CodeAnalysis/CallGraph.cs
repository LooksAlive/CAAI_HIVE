using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CAAI.CallGraphVisualizer;

// ---------------- Model ----------------

public sealed class CallNode
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string FullName { get; init; } = "";
    public string ContainingType { get; init; } = "";
    public string ContainingNamespace { get; init; } = "";
    public string AssemblyName { get; init; } = "";
    public bool IsExternal { get; init; }
    public int CallCount { get; set; }

    // Layout (filled by GraphLayoutEngine)
    public double X { get; set; }
    public double Y { get; set; }
    public double Radius { get; set; } = 24;
    public double Width { get; set; } = 160;
    public double Height { get; set; } = 40;
}

public sealed class CallEdge
{
    public string FromId { get; init; } = "";
    public string ToId { get; init; } = "";
    public int Weight { get; set; } = 1;
    public List<TextSpan> CallSites { get; init; } = new();
}

public sealed class CallGraph
{
    public Dictionary<string, CallNode> Nodes { get; } = new();
    public List<CallEdge> Edges { get; } = new();

    public CallNode GetOrAddNode(IMethodSymbol symbol)
    {
        var id = GetId(symbol);
        if (Nodes.TryGetValue(id, out var existing)) return existing;

        var node = new CallNode
        {
            Id = id,
            DisplayName = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            FullName = symbol.ToDisplayString(),
            ContainingType = symbol.ContainingType?.ToDisplayString() ?? "<global>",
            ContainingNamespace = symbol.ContainingNamespace?.ToDisplayString() ?? "<global>",
            AssemblyName = symbol.ContainingAssembly?.Name ?? "?",
            IsExternal = symbol.Locations.All(l => !l.IsInSource),
        };
        Nodes[id] = node;
        return node;
    }

    public void AddEdge(IMethodSymbol from, IMethodSymbol to, TextSpan site)
    {
        if (SymbolEqualityComparer.Default.Equals(from, to)) return;

        var fromNode = GetOrAddNode(from);
        var toNode = GetOrAddNode(to);
        fromNode.CallCount++;

        var edge = Edges.FirstOrDefault(e => e.FromId == fromNode.Id && e.ToId == toNode.Id);
        if (edge == null)
        {
            edge = new CallEdge { FromId = fromNode.Id, ToId = toNode.Id };
            Edges.Add(edge);
        }
        edge.Weight++;
        edge.CallSites.Add(site);
    }

    public static string GetId(IMethodSymbol symbol) =>
        symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) + "|" +
        string.Join(",", symbol.Parameters.Select(p => p.Type.ToDisplayString()));
}

// ---------------- Extractor ----------------

public sealed class CallGraphExtractor
{
    private readonly Compilation _compilation;

    public CallGraphExtractor(Compilation compilation)
    {
        _compilation = compilation ?? throw new ArgumentNullException(nameof(compilation));
    }

    public async Task<CallGraph> ExtractAsync(
        bool includeExternalCalls = false,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        var graph = new CallGraph();

        foreach (var tree in _compilation.SyntaxTrees)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report($"Scanning {tree.FilePath}");

            var model = _compilation.GetSemanticModel(tree);
            var root = await tree.GetRootAsync(ct).ConfigureAwait(false);

            // For every method declaration, find all invocations inside it.
            var methodDecls = root.DescendantNodes().OfType<MethodDeclarationSyntax>();
            foreach (var decl in methodDecls)
            {
                ct.ThrowIfCancellationRequested();

                if (model.GetDeclaredSymbol(decl, ct) is not IMethodSymbol caller) continue;

                var invocations = decl.DescendantNodes()
                    .OfType<InvocationExpressionSyntax>();

                foreach (var inv in invocations)
                {
                    var info = model.GetSymbolInfo(inv, ct);
                    var target = info.Symbol as IMethodSymbol
                                 ?? info.CandidateSymbols.FirstOrDefault() as IMethodSymbol;
                    if (target == null) continue;

                    // Reduce constructed generics back to their definition for stable identity.
                    if (target.IsGenericMethod && !target.IsDefinition)
                        target = target.OriginalDefinition;

                    bool inSource = target.Locations.Any(l => l.IsInSource);
                    if (!inSource && !includeExternalCalls) continue;

                    graph.AddEdge(caller, target, inv.Span);
                }
            }
        }

        progress?.Report($"Done. {graph.Nodes.Count} nodes, {graph.Edges.Count} edges.");
        return graph;
    }

    /// <summary>
    /// Alternative: use SymbolFinder to enrich an existing graph with callers from the whole solution.
    /// Useful when the Compilation is a single project but you want cross-project callers.
    /// </summary>
    public static async Task EnrichWithSolutionCallersAsync(
        CallGraph graph,
        Solution solution,
        CancellationToken ct = default)
    {
        foreach (var nodeId in graph.Nodes.Keys.ToList())
        {
            ct.ThrowIfCancellationRequested();
            // Find the symbol for this node id via its declaration syntax — omitted here;
            // a full solution pass is expensive. Prefer ExtractAsync on a full Compilation.
        }
        await Task.CompletedTask;
    }
}

// ---------------- Layout ----------------

public enum LayoutKind { Layered, Grid, Circular }

public sealed class GraphLayoutEngine
{
    public double NodeWidth { get; set; } = 180;
    public double NodeHeight { get; set; } = 44;
    public double HorizontalGap { get; set; } = 60;
    public double VerticalGap { get; set; } = 90;

    public void Layout(CallGraph graph, LayoutKind kind = LayoutKind.Layered)
    {
        foreach (var n in graph.Nodes.Values)
        {
            n.Width = NodeWidth;
            n.Height = NodeHeight;
        }

        switch (kind)
        {
            case LayoutKind.Layered: DoLayered(graph); break;
            case LayoutKind.Grid: DoGrid(graph); break;
            case LayoutKind.Circular: DoCircular(graph); break;
        }
    }

    private void DoLayered(CallGraph graph)
    {
        // Longest-path layering: level(n) = max(level(caller)) + 1 for each incoming edge.
        var incoming = graph.Nodes.Keys.ToDictionary(k => k, _ => new List<string>());
        foreach (var e in graph.Edges)
            if (incoming.ContainsKey(e.ToId) && incoming.ContainsKey(e.FromId))
                incoming[e.ToId].Add(e.FromId);

        var level = new Dictionary<string, int>();
        var visiting = new HashSet<string>();

        int GetLevel(string id)
        {
            if (level.TryGetValue(id, out var l)) return l;
            if (!visiting.Add(id)) return 0; // cycle guard
            int best = 0;
            foreach (var caller in incoming[id])
                best = Math.Max(best, GetLevel(caller) + 1);
            visiting.Remove(id);
            level[id] = best;
            return best;
        }

        foreach (var id in graph.Nodes.Keys) GetLevel(id);

        var byLevel = graph.Nodes.Values
            .GroupBy(n => level[n.Id])
            .OrderBy(g => g.Key);

        double y = 40;
        foreach (var group in byLevel)
        {
            var ordered = group.OrderBy(n => n.ContainingType).ThenBy(n => n.DisplayName).ToList();
            double totalWidth = ordered.Count * (NodeWidth + HorizontalGap) - HorizontalGap;
            double x = 40 + (Math.Max(0, 1400 - totalWidth) / 2);
            foreach (var n in ordered)
            {
                n.X = x;
                n.Y = y;
                x += NodeWidth + HorizontalGap;
            }
            y += NodeHeight + VerticalGap;
        }
    }

    private void DoGrid(CallGraph graph)
    {
        var nodes = graph.Nodes.Values.OrderBy(n => n.FullName).ToList();
        int cols = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(nodes.Count)));
        for (int i = 0; i < nodes.Count; i++)
        {
            int r = i / cols, c = i % cols;
            nodes[i].X = 40 + c * (NodeWidth + HorizontalGap);
            nodes[i].Y = 40 + r * (NodeHeight + VerticalGap);
        }
    }

    private void DoCircular(CallGraph graph)
    {
        var nodes = graph.Nodes.Values.OrderBy(n => n.FullName).ToList();
        int count = Math.Max(1, nodes.Count);
        double radius = Math.Max(200, count * (NodeWidth + HorizontalGap) / (2 * Math.PI));
        double cx = radius + 100, cy = radius + 100;
        for (int i = 0; i < count; i++)
        {
            double angle = 2 * Math.PI * i / count - Math.PI / 2;
            var n = nodes[i];
            n.X = cx + radius * Math.Cos(angle) - NodeWidth / 2;
            n.Y = cy + radius * Math.Sin(angle) - NodeHeight / 2;
        }
    }
}