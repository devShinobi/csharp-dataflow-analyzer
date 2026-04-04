using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CSharpDataFlowAnalyzer;

// ── Top-level output ─────────────────────────────────────────────────────────

public class FlowGraph
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("analysisDepth")]
    public string AnalysisDepth { get; set; } = "method+inter-method+inter-class";

    [JsonPropertyName("units")]
    public List<ClassUnit> Units { get; set; } = new();

    [JsonPropertyName("flowEdges")]
    public List<FlowEdge> FlowEdges { get; set; } = new();
}

// ── Class-level ──────────────────────────────────────────────────────────────

public class ClassUnit
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("namespace")]
    public string? Namespace { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "class"; // class | interface | struct | record

    [JsonPropertyName("baseTypes")]
    public List<string> BaseTypes { get; set; } = new();

    [JsonPropertyName("fields")]
    public List<FieldNode> Fields { get; set; } = new();

    [JsonPropertyName("properties")]
    public List<PropertyNode> Properties { get; set; } = new();

    [JsonPropertyName("constructors")]
    public List<MethodNode> Constructors { get; set; } = new();

    [JsonPropertyName("methods")]
    public List<MethodNode> Methods { get; set; } = new();
}

// ── Field / Property ─────────────────────────────────────────────────────────

public class FieldNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("accessibility")]
    public string Accessibility { get; set; } = "";

    [JsonPropertyName("isReadonly")]
    public bool IsReadonly { get; set; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }

    [JsonPropertyName("initializer")]
    public string? Initializer { get; set; }
}

public class PropertyNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("accessibility")]
    public string Accessibility { get; set; } = "";

    [JsonPropertyName("hasGetter")]
    public bool HasGetter { get; set; }

    [JsonPropertyName("hasSetter")]
    public bool HasSetter { get; set; }

    [JsonPropertyName("isAutoProperty")]
    public bool IsAutoProperty { get; set; }
}

// ── Method ───────────────────────────────────────────────────────────────────

public class MethodNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("returnType")]
    public string ReturnType { get; set; } = "";

    [JsonPropertyName("accessibility")]
    public string Accessibility { get; set; } = "";

    [JsonPropertyName("isAsync")]
    public bool IsAsync { get; set; }

    [JsonPropertyName("isStatic")]
    public bool IsStatic { get; set; }

    [JsonPropertyName("params")]
    public List<ParamNode> Params { get; set; } = new();

    [JsonPropertyName("locals")]
    public List<LocalNode> Locals { get; set; } = new();

    [JsonPropertyName("assignments")]
    public List<AssignmentNode> Assignments { get; set; } = new();

    [JsonPropertyName("calls")]
    public List<CallNode> Calls { get; set; } = new();

    [JsonPropertyName("returns")]
    public List<ReturnNode> Returns { get; set; } = new();

    [JsonPropertyName("flowEdges")]
    public List<FlowEdge> FlowEdges { get; set; } = new();

    // Populated by FlowEnricher
    [JsonPropertyName("linqChains")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<LinqChain>? LinqChains { get; set; }
}

// ── Parameters ───────────────────────────────────────────────────────────────

public class ParamNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("hasDefault")]
    public bool HasDefault { get; set; }

    [JsonPropertyName("defaultValue")]
    public string? DefaultValue { get; set; }

    [JsonPropertyName("modifier")]
    public string? Modifier { get; set; } // ref | out | in
}

// ── Local variable ────────────────────────────────────────────────────────────

public class LocalNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("isVar")]
    public bool IsVar { get; set; }

    [JsonPropertyName("initExpression")]
    public string? InitExpression { get; set; }

    [JsonPropertyName("dataSourceIds")]
    public List<string> DataSourceIds { get; set; } = new();

    // Populated by FlowEnricher
    [JsonPropertyName("hasConditionalInit")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool HasConditionalInit { get; set; }

    [JsonPropertyName("conditionalKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConditionalKind { get; set; }  // ternary | switch-expression | null-coalescing

    [JsonPropertyName("objectInitAssignments")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<ObjectInitAssignment>? ObjectInitAssignments { get; set; }
}

// ── Assignment ────────────────────────────────────────────────────────────────

public class AssignmentNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("target")]
    public string Target { get; set; } = "";

    [JsonPropertyName("targetId")]
    public string? TargetId { get; set; }

    [JsonPropertyName("targetKind")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TargetKind { get; set; }  // field | property | local — set by enricher

    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "=";

    [JsonPropertyName("sourceIds")]
    public List<string> SourceIds { get; set; } = new();

    [JsonPropertyName("sequenceOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SequenceOrder { get; set; }
}

// ── Call ──────────────────────────────────────────────────────────────────────

public class CallNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("methodName")]
    public string MethodName { get; set; } = "";

    [JsonPropertyName("receiver")]
    public string? Receiver { get; set; }

    [JsonPropertyName("receiverKind")]
    public string? ReceiverKind { get; set; } // field | param | local | this | static | unknown

    [JsonPropertyName("isAwaited")]
    public bool IsAwaited { get; set; }

    [JsonPropertyName("isChained")]
    public bool IsChained { get; set; }

    [JsonPropertyName("arguments")]
    public List<ArgumentNode> Arguments { get; set; } = new();

    [JsonPropertyName("resultAssignedTo")]
    public string? ResultAssignedTo { get; set; }

    [JsonPropertyName("resultAssignedToId")]
    public string? ResultAssignedToId { get; set; }

    [JsonPropertyName("resolvedMethodId")]
    public string? ResolvedMethodId { get; set; }

    [JsonPropertyName("sequenceOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SequenceOrder { get; set; }
}

// ── Argument ──────────────────────────────────────────────────────────────────

public class ArgumentNode
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("sourceId")]
    public string? SourceId { get; set; }

    [JsonPropertyName("modifier")]
    public string? Modifier { get; set; }
}

// ── Return ────────────────────────────────────────────────────────────────────

public class ReturnNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("expression")]
    public string? Expression { get; set; }

    [JsonPropertyName("sourceIds")]
    public List<string> SourceIds { get; set; } = new();

    [JsonPropertyName("isInterpolated")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsInterpolated { get; set; }

    [JsonPropertyName("sequenceOrder")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int SequenceOrder { get; set; }
}

// ── Flow edges ────────────────────────────────────────────────────────────────

public class FlowEdge
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
    // assignment | argument | return | field-write | field-read | property-read | property-write
    // inter-method | inter-class | initialization

    [JsonPropertyName("label")]
    public string? Label { get; set; }
}

// ── Enricher types ────────────────────────────────────────────────────────────

public class LinqChain
{
    [JsonPropertyName("steps")]
    public List<LinqChainStep> Steps { get; set; } = new();
}

public class LinqChainStep
{
    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("callId")]
    public string CallId { get; set; } = "";

    [JsonPropertyName("methodName")]
    public string MethodName { get; set; } = "";
}

public class ObjectInitAssignment
{
    [JsonPropertyName("property")]
    public string Property { get; set; } = "";

    [JsonPropertyName("valueExpression")]
    public string ValueExpression { get; set; } = "";
}
