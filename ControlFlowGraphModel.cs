using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CallGraphVisualizer;

public sealed class CfgNode
{
    public string Id { get; init; } = "";
    public int Ordinal { get; init; }
    public string Kind { get; init; } = "";
    public string Label { get; init; } = "";
    public bool IsEntry { get; init; }
    public bool IsExit { get; init; }

    // 复用布局字段
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 200;
    public double Height { get; set; } = 60;
    public double Radius { get; set; }
}

public sealed class CfgEdge
{
    public string FromId { get; init; } = "";
    public string ToId { get; init; } = "";
    public string? Condition { get; init; } // "True"/"False"/"Switch"/null
    public bool IsBackEdge { get; set; }
}

public sealed class ControlFlowGraphModel
{
    public string MethodName { get; init; } = "";
    public List<CfgNode> Nodes { get; } = new();
    public List<CfgEdge> Edges { get; } = new();

    public static ControlFlowGraphModel FromOperation(IMethodBodyOperation methodBody, string methodDisplayName)
    {
        var cfg = ControlFlowGraph.Create(methodBody);
        return FromRoslynCfg(cfg, methodDisplayName);
    }

    public static ControlFlowGraphModel FromSyntax(SyntaxNode node, SemanticModel model, string methodDisplayName)
    {
        var cfg = ControlFlowGraph.Create(node, model);
        if (cfg == null) throw new InvalidOperationException("Could not create CFG for node.");
        return FromRoslynCfg(cfg, methodDisplayName);
    }

    private static ControlFlowGraphModel FromRoslynCfg(ControlFlowGraph cfg, string methodName)
    {
        var model = new ControlFlowGraphModel { MethodName = methodName };
        var blockToNode = new Dictionary<BasicBlock, CfgNode>();

        foreach (var block in cfg.Blocks)
        {
            var ordinal = block.Ordinal;
            var kind = block.Kind.ToString(); // Entry / Exit / Block / etc.
            var firstOp = block.Operations.FirstOrDefault();
            var label = firstOp != null
                ? $"{kind} #{ordinal}\n{firstOp.Kind}"
                : $"{kind} #{ordinal}";

            var node = new CfgNode
            {
                Id = $"B{ordinal}",
                Ordinal = ordinal,
                Kind = kind,
                Label = label,
                IsEntry = block.Kind == BasicBlockKind.Entry,
                IsExit = block.Kind == BasicBlockKind.Exit,
            };
            model.Nodes.Add(node);
            blockToNode[block] = node;
        }

        foreach (var block in cfg.Blocks)
        {
            var from = blockToNode[block];

            // Fall-through branch
            if (block.FallThroughSuccessor != null)
            {
                var succ = block.FallThroughSuccessor;
                if (blockToNode.TryGetValue(succ.Destination, out var to))
                {
                    model.Edges.Add(new CfgEdge
                    {
                        FromId = from.Id,
                        ToId = to.Id,
                        Condition = succ.Semantics == ControlFlowBranchSemantics.Regular ? null : succ.Semantics.ToString(),
                    });
                }
            }

            // Conditional branch (if/switch)
            if (block.ConditionalSuccessor != null)
            {
                var succ = block.ConditionalSuccessor;
                if (blockToNode.TryGetValue(succ.Destination, out var to))
                {
                    model.Edges.Add(new CfgEdge
                    {
                        FromId = from.Id,
                        ToId = to.Id,
                        Condition = succ.Semantics.ToString(),
                    });
                }
            }
        }

        // Mark back edges (simple heuristic: target ordinal <= source ordinal and not entry)
        foreach (var e in model.Edges)
        {
            var fromOrd = model.Nodes.First(n => n.Id == e.FromId).Ordinal;
            var toOrd = model.Nodes.First(n => n.Id == e.ToId).Ordinal;
            e.IsBackEdge = toOrd <= fromOrd && toOrd != 0;
        }

        return model;
    }

    // 简单网格布局，够用
    public void ApplyGridLayout(int cols = 4)
    {
        for (int i = 0; i < Nodes.Count; i++)
        {
            Nodes[i].X = 60 + (i % cols) * 260;
            Nodes[i].Y = 60 + (i / cols) * 120;
        }
    }
}



// usage:
/*
var methodOp = semanticModel.GetOperation(methodDecl) as IMethodBodyOperation;
var cfgModel = ControlFlowGraphModel.FromOperation(methodOp, methodDecl.Identifier.Text);
cfgModel.ApplyGridLayout();

// 复用之前的视图：需要把 CfgNode 适配成 CallNode 或直接改 CallGraphView
// 最简单的方式：在 CallGraphView 里加一个重载 SetGraph(ControlFlowGraphModel)
*/

