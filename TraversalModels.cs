using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CSharpDataFlowAnalyzer;

public enum TraversalDirection { Forward, Backward }

// ── Traversal result ──────────────────────────────────────────────────────────

public class TraversalResult
{
    [JsonPropertyName("originSymbolId")]
    public string OriginSymbolId { get; set; } = "";

    [JsonPropertyName("direction")]
    public string Direction { get; set; } = ""; // forward | backward

    [JsonPropertyName("maxDepth")]
    public int MaxDepth { get; set; }

    [JsonPropertyName("nodesVisited")]
    public int NodesVisited { get; set; }

    [JsonPropertyName("maxDepthReached")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool MaxDepthReached { get; set; }

    [JsonPropertyName("mutationsFound")]
    public int MutationsFound { get; set; }

    [JsonPropertyName("cyclesDetected")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? CyclesDetected { get; set; }

    [JsonPropertyName("root")]
    public TraversalNode Root { get; set; } = new();
}

// ── Traversal node ────────────────────────────────────────────────────────────
// One node per symbol visited in the traversal tree.
// isCycleRef=true means the symbol was already expanded elsewhere in the tree;
// children will be empty to prevent duplication.

public class TraversalNode
{
    [JsonPropertyName("symbolId")]
    public string SymbolId { get; set; } = "";

    [JsonPropertyName("symbolKind")]
    public string SymbolKind { get; set; } = "";
    // field | property | param | local | method | constructor | call | assignment | return | class | unknown

    [JsonPropertyName("edgeKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EdgeKind { get; set; } // FlowEdge.Kind that led to this node (null for root)

    [JsonPropertyName("edgeLabel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? EdgeLabel { get; set; } // FlowEdge.Label that led to this node

    [JsonPropertyName("depth")]
    public int Depth { get; set; }

    [JsonPropertyName("isCycleRef")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsCycleRef { get; set; }

    [JsonPropertyName("mutations")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TraversalMutation>? Mutations { get; set; }

    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<TraversalNode>? Children { get; set; }
}

// ── Traversal mutation ────────────────────────────────────────────────────────
// Projection of MutationNode attached to the traversal node that owns the symbol.
// TargetSymbolId is omitted — it is the enclosing TraversalNode.SymbolId.

public class TraversalMutation
{
    [JsonPropertyName("mutationId")]
    public string MutationId { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
    // property-set | field-write | index-write | collection-add | collection-remove
    // collection-sort | external-call | ref-write

    [JsonPropertyName("targetMember")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetMember { get; set; }

    [JsonPropertyName("sourceExpression")]
    public string SourceExpression { get; set; } = "";

    [JsonPropertyName("methodId")]
    public string MethodId { get; set; } = "";

    [JsonPropertyName("isInsideLoop")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsInsideLoop { get; set; }

    [JsonPropertyName("isOnSharedObject")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsOnSharedObject { get; set; }

    [JsonPropertyName("guard")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConditionGuard? Guard { get; set; }
}
