using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CSharpDataFlowAnalyzer;

// ── Top-level onboarding output ──────────────────────────────────────────────

public class OnboardingOutput
{
    [JsonPropertyName("dependencyGraph")]
    public DependencyGraph DependencyGraph { get; set; } = new();

    [JsonPropertyName("entryPoints")]
    public List<EntryPoint> EntryPoints { get; set; } = new();

    [JsonPropertyName("classRelationships")]
    public List<ClassRelationship> ClassRelationships { get; set; } = new();

    [JsonPropertyName("hotNodes")]
    public List<HotNode> HotNodes { get; set; } = new();

    [JsonPropertyName("classExplanation")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ClassExplanation? ClassExplanation { get; set; }

    [JsonPropertyName("classExplanations")]
    public Dictionary<string, ClassExplanation> ClassExplanations { get; set; } = new();

    [JsonPropertyName("results")]
    public List<AnalysisResult> Results { get; set; } = new();
}

// ── Dependency graph ─────────────────────────────────────────────────────────

public class DependencyGraph
{
    [JsonPropertyName("projectDependencies")]
    public List<DependencyEdge> ProjectDependencies { get; set; } = new();

    [JsonPropertyName("namespaceDependencies")]
    public List<DependencyEdge> NamespaceDependencies { get; set; } = new();

    [JsonPropertyName("classDependencies")]
    public List<DependencyEdge> ClassDependencies { get; set; } = new();
}

public class DependencyEdge
{
    [JsonPropertyName("from")]
    public string From { get; set; } = "";

    [JsonPropertyName("to")]
    public string To { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
    // inheritance | interface-impl | field-type | constructor-param |
    // method-call | property-type | return-type | parameter-type

    [JsonPropertyName("weight")]
    public int Weight { get; set; } = 1;
}

// ── Class relationships ──────────────────────────────────────────────────────

public class ClassRelationship
{
    [JsonPropertyName("classId")]
    public string ClassId { get; set; } = "";

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = "";

    [JsonPropertyName("namespace")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Namespace { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("dependsOn")]
    public List<DependencyRef> DependsOn { get; set; } = new();

    [JsonPropertyName("dependedOnBy")]
    public List<DependencyRef> DependedOnBy { get; set; } = new();

    [JsonPropertyName("constructorParams")]
    public List<ConstructorParam> ConstructorParams { get; set; } = new();

    [JsonPropertyName("implementedInterfaces")]
    public List<string> ImplementedInterfaces { get; set; } = new();

    [JsonPropertyName("baseClass")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BaseClass { get; set; }

    [JsonPropertyName("fanIn")]
    public int FanIn { get; set; }

    [JsonPropertyName("fanOut")]
    public int FanOut { get; set; }
}

public class DependencyRef
{
    [JsonPropertyName("classId")]
    public string ClassId { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
}

public class ConstructorParam
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("resolvedClassId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ResolvedClassId { get; set; }
}

// ── Entry points ─────────────────────────────────────────────────────────────

public class EntryPoint
{
    [JsonPropertyName("classId")]
    public string ClassId { get; set; } = "";

    [JsonPropertyName("methodId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? MethodId { get; set; }

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
    // main | controller-action | minimal-api | background-service |
    // mediatr-handler | grpc-service | event-handler

    [JsonPropertyName("httpMethod")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HttpMethod { get; set; }

    [JsonPropertyName("route")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Route { get; set; }
}

// ── Hot nodes ────────────────────────────────────────────────────────────────

public class HotNode
{
    [JsonPropertyName("classId")]
    public string ClassId { get; set; } = "";

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = "";

    [JsonPropertyName("fanIn")]
    public int FanIn { get; set; }

    [JsonPropertyName("fanOut")]
    public int FanOut { get; set; }

    [JsonPropertyName("totalConnections")]
    public int TotalConnections { get; set; }

    [JsonPropertyName("rank")]
    public int Rank { get; set; }

    [JsonPropertyName("role")]
    public string Role { get; set; } = "";
    // hub | provider | consumer | leaf
}

// ── Class explanation ────────────────────────────────────────────────────────

public class ClassExplanation
{
    [JsonPropertyName("classId")]
    public string ClassId { get; set; } = "";

    [JsonPropertyName("className")]
    public string ClassName { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("responsibilities")]
    public List<string> Responsibilities { get; set; } = new();

    [JsonPropertyName("dependencies")]
    public ClassRelationship Dependencies { get; set; } = new();

    [JsonPropertyName("state")]
    public ClassState State { get; set; } = new();

    [JsonPropertyName("publicApi")]
    public List<MethodSummary> PublicApi { get; set; } = new();

    [JsonPropertyName("keyFlows")]
    public List<KeyFlow> KeyFlows { get; set; } = new();

    [JsonPropertyName("isEntryPoint")]
    public bool IsEntryPoint { get; set; }

    [JsonPropertyName("hotRank")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? HotRank { get; set; }
}

public class ClassState
{
    [JsonPropertyName("mutableFields")]
    public List<string> MutableFields { get; set; } = new();

    [JsonPropertyName("readonlyFields")]
    public List<string> ReadonlyFields { get; set; } = new();

    [JsonPropertyName("properties")]
    public List<string> Properties { get; set; } = new();

    [JsonPropertyName("hasMutableState")]
    public bool HasMutableState { get; set; }
}

public class MethodSummary
{
    [JsonPropertyName("methodId")]
    public string MethodId { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("returnType")]
    public string ReturnType { get; set; } = "";

    [JsonPropertyName("paramCount")]
    public int ParamCount { get; set; }

    [JsonPropertyName("callsOut")]
    public int CallsOut { get; set; }

    [JsonPropertyName("isAsync")]
    public bool IsAsync { get; set; }
}

public class KeyFlow
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("methodChain")]
    public List<string> MethodChain { get; set; } = new();
}
