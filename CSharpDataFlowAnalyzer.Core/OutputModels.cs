using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CSharpDataFlowAnalyzer;

// ── Top-level output types ────────────────────────────────────────────────────

public class AnalysisResult
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("flowGraph")]
    public FlowGraph FlowGraph { get; set; } = new();

    [JsonPropertyName("mutationGraph")]
    public MutationGraph MutationGraph { get; set; } = new();

    [JsonPropertyName("traversal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TraversalResult? Traversal { get; set; }
}

/// <summary>
/// Wraps multi-file results when a traversal is requested.
/// Without --trace-*, multi-file output remains a plain JSON array.
/// </summary>
public class MultiFileOutput
{
    [JsonPropertyName("results")]
    public List<AnalysisResult> Results { get; set; } = new();

    [JsonPropertyName("traversal")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TraversalResult? Traversal { get; set; }
}
