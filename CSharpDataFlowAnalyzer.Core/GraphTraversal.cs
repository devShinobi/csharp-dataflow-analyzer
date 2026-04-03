using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpDataFlowAnalyzer;

// ── Graph traversal engine ────────────────────────────────────────────────────
// Builds forward/backward edge indexes and mutation indexes once, then answers
// reachability queries as traversal trees.
//
// Forward  ("what does X affect?")  — follows FlowEdge.From → FlowEdge.To
// Backward ("what feeds into X?")   — follows FlowEdge.To   → FlowEdge.From

public class GraphTraversalEngine
{
    private readonly Dictionary<string, List<FlowEdge>> _forwardIndex  = new();
    private readonly Dictionary<string, List<FlowEdge>> _backwardIndex = new();

    // Primary: targetSymbolId → mutations that directly write to that symbol
    private readonly Dictionary<string, List<MutationNode>> _mutationIndex = new();

    // Alias: aliasId → mutations whose affectedAliasIds list contains that id
    private readonly Dictionary<string, List<MutationNode>> _aliasIndex = new();

    public GraphTraversalEngine(FlowGraph flowGraph, MutationGraph mutationGraph)
    {
        BuildEdgeIndexes(flowGraph);
        BuildMutationIndexes(mutationGraph);
    }

    // Factory for multi-file analysis: merges all graphs into one engine so
    // cross-file edges are traversable from a single starting symbol.
    public static GraphTraversalEngine FromMultiple(IEnumerable<AnalysisResult> results)
    {
        var mergedFlow     = new FlowGraph();
        var mergedMutation = new MutationGraph();

        foreach (var r in results)
        {
            mergedFlow.Units.AddRange(r.FlowGraph.Units);
            mergedFlow.FlowEdges.AddRange(r.FlowGraph.FlowEdges);
            mergedMutation.Symbols.AddRange(r.MutationGraph.Symbols);
            mergedMutation.Mutations.AddRange(r.MutationGraph.Mutations);
            mergedMutation.StateChangeSummaries.AddRange(r.MutationGraph.StateChangeSummaries);
        }

        return new GraphTraversalEngine(mergedFlow, mergedMutation);
    }

    // ── Public API ────────────────────────────────────────────────────────────

    public bool SymbolExists(string symbolId) =>
        _forwardIndex.ContainsKey(symbolId)
        || _backwardIndex.ContainsKey(symbolId)
        || _mutationIndex.ContainsKey(symbolId);

    /// <summary>Traverse the graph from <paramref name="symbolId"/>.</summary>
    /// <param name="direction"><see cref="TraversalDirection.Forward"/> follows what X affects;
    /// <see cref="TraversalDirection.Backward"/> follows what feeds into X.</param>
    /// <param name="maxDepth">Maximum edge hops before stopping expansion (default 20).</param>
    public TraversalResult Traverse(string symbolId, TraversalDirection direction, int maxDepth = 20)
    {
        var visited          = new HashSet<string>(StringComparer.Ordinal);
        var cycleSet         = new HashSet<string>(StringComparer.Ordinal);
        var cyclesDetected   = new List<string>();
        bool maxDepthReached = false;
        int  mutationsFound  = 0;

        var root = BuildNode(
            symbolId, edgeKind: null, edgeLabel: null, depth: 0,
            direction, maxDepth, visited, cycleSet, cyclesDetected,
            ref maxDepthReached, ref mutationsFound);

        return new TraversalResult
        {
            OriginSymbolId  = symbolId,
            Direction       = direction == TraversalDirection.Forward ? "forward" : "backward",
            MaxDepth        = maxDepth,
            NodesVisited    = visited.Count,
            MaxDepthReached = maxDepthReached,
            MutationsFound  = mutationsFound,
            CyclesDetected  = cyclesDetected.Count > 0 ? cyclesDetected : null,
            Root            = root
        };
    }

    // ── Index builders ────────────────────────────────────────────────────────

    private void BuildEdgeIndexes(FlowGraph flowGraph)
    {
        // Collect top-level inter-method/inter-class edges plus all per-method
        // intra-method edges into one pass.
        var allEdges = new List<FlowEdge>(flowGraph.FlowEdges);
        foreach (var unit in flowGraph.Units)
            foreach (var method in unit.Methods.Concat(unit.Constructors))
                allEdges.AddRange(method.FlowEdges);

        foreach (var edge in allEdges)
        {
            if (!string.IsNullOrEmpty(edge.From))
                IndexEdge(_forwardIndex, edge.From, edge);

            if (!string.IsNullOrEmpty(edge.To))
                IndexEdge(_backwardIndex, edge.To, edge);
        }
    }

    private static void IndexEdge(
        Dictionary<string, List<FlowEdge>> index, string key, FlowEdge edge)
    {
        if (!index.TryGetValue(key, out var list))
            index[key] = list = new List<FlowEdge>();
        list.Add(edge);
    }

    private void BuildMutationIndexes(MutationGraph mutationGraph)
    {
        foreach (var mutation in mutationGraph.Mutations)
        {
            IndexMutation(_mutationIndex, mutation.TargetSymbolId, mutation);

            foreach (var aliasId in mutation.AffectedAliasIds)
                IndexMutation(_aliasIndex, aliasId, mutation);
        }
    }

    private static void IndexMutation(
        Dictionary<string, List<MutationNode>> index, string key, MutationNode mutation)
    {
        if (string.IsNullOrEmpty(key)) return;
        if (!index.TryGetValue(key, out var list))
            index[key] = list = new List<MutationNode>();
        list.Add(mutation);
    }

    // ── Recursive tree builder ────────────────────────────────────────────────
    // maxDepth is bounded (default 20), so recursion depth is safe.
    // The shared `visited` set prevents revisiting — a node already expanded
    // elsewhere in the tree is rendered as isCycleRef=true with no children.

    private TraversalNode BuildNode(
        string symbolId,
        string? edgeKind,
        string? edgeLabel,
        int depth,
        TraversalDirection direction,
        int maxDepth,
        HashSet<string> visited,
        HashSet<string> cycleSet,
        List<string> cyclesDetected,
        ref bool maxDepthReached,
        ref int mutationsFound)
    {
        var node = new TraversalNode
        {
            SymbolId   = symbolId,
            SymbolKind = ResolveSymbolKind(symbolId),
            EdgeKind   = edgeKind,
            EdgeLabel  = edgeLabel,
            Depth      = depth
        };

        // Attach mutations — but count only on first (non-cycle) visit below.
        var mutations = AttachMutations(symbolId);
        if (mutations.Count > 0)
            node.Mutations = mutations;

        // Cycle: already expanded this symbol somewhere else in the tree.
        if (visited.Contains(symbolId))
        {
            node.IsCycleRef = true;
            if (cycleSet.Add(symbolId))
                cyclesDetected.Add(symbolId);
            return node;
        }

        visited.Add(symbolId);

        // Count mutations only for the first (authoritative) visit.
        mutationsFound += mutations.Count;

        // Depth limit: record the fact but still return the node with its mutations.
        if (depth >= maxDepth)
        {
            maxDepthReached = true;
            return node;
        }

        node.Children = ExpandChildren(
            symbolId, direction, depth, maxDepth,
            visited, cycleSet, cyclesDetected,
            ref maxDepthReached, ref mutationsFound);

        return node;
    }

    private List<TraversalNode>? ExpandChildren(
        string symbolId,
        TraversalDirection direction,
        int depth,
        int maxDepth,
        HashSet<string> visited,
        HashSet<string> cycleSet,
        List<string> cyclesDetected,
        ref bool maxDepthReached,
        ref int mutationsFound)
    {
        var index = direction == TraversalDirection.Forward ? _forwardIndex : _backwardIndex;
        if (!index.TryGetValue(symbolId, out var edges) || edges.Count == 0)
            return null;

        var children = new List<TraversalNode>();
        foreach (var edge in edges)
        {
            var nextId = direction == TraversalDirection.Forward ? edge.To : edge.From;
            if (string.IsNullOrEmpty(nextId)) continue;

            children.Add(BuildNode(
                nextId, edge.Kind, edge.Label, depth + 1,
                direction, maxDepth, visited, cycleSet, cyclesDetected,
                ref maxDepthReached, ref mutationsFound));
        }

        return children.Count > 0 ? children : null;
    }

    // ── Mutation attachment ───────────────────────────────────────────────────

    private List<TraversalMutation> AttachMutations(string symbolId)
    {
        var result = new List<TraversalMutation>();
        var seen   = new HashSet<string>(StringComparer.Ordinal);

        if (_mutationIndex.TryGetValue(symbolId, out var direct))
            foreach (var m in direct)
                if (seen.Add(m.Id))
                    result.Add(ProjectMutation(m));

        if (_aliasIndex.TryGetValue(symbolId, out var aliased))
            foreach (var m in aliased)
                if (seen.Add(m.Id))
                    result.Add(ProjectMutation(m));

        return result;
    }

    private static TraversalMutation ProjectMutation(MutationNode m) => new()
    {
        MutationId       = m.Id,
        Kind             = m.Kind,
        TargetMember     = m.TargetMember,
        SourceExpression = m.SourceExpression,
        MethodId         = m.MethodId,
        IsInsideLoop     = m.IsInsideLoop,
        IsOnSharedObject = m.IsOnSharedObject,
        Guard            = m.Guard
    };

    // ── Symbol kind resolver ──────────────────────────────────────────────────
    // Infers the kind of a symbol from its ID string format (see IdGen.cs).

    private static string ResolveSymbolKind(string id)
    {
        if (id.Contains("/param:"))                    return "param";
        if (id.Contains("/local:"))                    return "local";
        if (id.Contains("/call:"))                     return "call";
        if (id.Contains("/assign:"))                   return "assignment";
        if (id.Contains("/return"))                    return "return";
        if (id.Contains("::field:"))                   return "field";
        if (id.Contains("::prop:"))                    return "property";
        if (id.Contains("::ctor["))                    return "constructor";
        if (id.Contains("::") && id.Contains("["))     return "method";
        if (id.Contains(".") || !id.Contains("::"))    return "class";
        return "unknown";
    }
}
