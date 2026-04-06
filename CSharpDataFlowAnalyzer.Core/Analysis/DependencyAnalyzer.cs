using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace CSharpDataFlowAnalyzer.Analysis;

/// <summary>
/// Post-processes AnalysisResult[] to build a multi-level dependency graph
/// (project → namespace → class) and per-class relationship summaries.
/// </summary>
internal sealed class DependencyAnalyzer
{
    private readonly List<AnalysisResult> _results;

    // classId → ClassUnit lookup
    private readonly Dictionary<string, ClassUnit> _classLookup = new();

    // type display name → classId (for resolving field/param types to known classes)
    private readonly Dictionary<string, string> _typeNameToClassId = new();

    // classId → source file path (for project-level grouping)
    private readonly Dictionary<string, string> _classToSource = new();

    private DependencyAnalyzer(List<AnalysisResult> results)
    {
        _results = results;
    }

    public static (DependencyGraph Graph, List<ClassRelationship> Relationships) Analyze(
        List<AnalysisResult> results,
        Action<string>? log = null)
    {
        var analyzer = new DependencyAnalyzer(results);
        analyzer.BuildLookups();

        var classDeps = analyzer.ExtractClassDependencies();
        var relationships = analyzer.BuildClassRelationships(classDeps);
        var namespaceDeps = AggregateToNamespaceLevel(classDeps);
        var projectDeps = analyzer.AggregateToProjectLevel(classDeps);

        var graph = new DependencyGraph
        {
            ProjectDependencies = projectDeps,
            NamespaceDependencies = namespaceDeps,
            ClassDependencies = classDeps
        };

        log?.Invoke($"  Dependency graph: {classDeps.Count} class edges, " +
                     $"{namespaceDeps.Count} namespace edges, " +
                     $"{projectDeps.Count} project edges, " +
                     $"{relationships.Count} class relationships");

        return (graph, relationships);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Step 1: Build lookup tables
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildLookups()
    {
        foreach (var result in _results)
        {
            foreach (var unit in result.FlowGraph.Units)
            {
                _classLookup[unit.Id] = unit;
                _classToSource[unit.Id] = result.Source;

                // Register both fully qualified ID and simple name for type resolution
                _typeNameToClassId[unit.Id] = unit.Id;

                // Also register by display-string format: "Namespace.ClassName"
                string displayName = unit.Namespace != null
                    ? $"{unit.Namespace}.{unit.Name}"
                    : unit.Name;
                _typeNameToClassId.TryAdd(displayName, unit.Id);

                // Register short name (last resort, may collide)
                _typeNameToClassId.TryAdd(unit.Name, unit.Id);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Step 2: Extract class-level dependency edges
    // ═══════════════════════════════════════════════════════════════════════════

    private List<DependencyEdge> ExtractClassDependencies()
    {
        // Use a dictionary to deduplicate and accumulate weights
        var edgeMap = new Dictionary<(string from, string to, string kind), int>();

        foreach (var (classId, unit) in _classLookup)
        {
            // Inheritance
            if (unit.BaseTypes.Count > 0)
            {
                string? baseClass = FindFirstNonInterface(unit.BaseTypes);
                if (baseClass != null)
                {
                    string? resolvedId = ResolveTypeName(baseClass);
                    if (resolvedId != null && resolvedId != classId)
                        AddEdge(edgeMap, classId, resolvedId, "inheritance");
                }

                // Interface implementations
                foreach (var bt in unit.BaseTypes)
                {
                    if (baseClass != null && bt == baseClass) continue;
                    string? resolvedId = ResolveTypeName(bt);
                    if (resolvedId != null && resolvedId != classId)
                        AddEdge(edgeMap, classId, resolvedId, "interface-impl");
                }
            }

            // Constructor params (DI dependencies)
            foreach (var ctor in unit.Constructors)
            {
                foreach (var param in ctor.Params)
                {
                    string? resolvedId = ResolveTypeName(param.Type);
                    if (resolvedId != null && resolvedId != classId)
                        AddEdge(edgeMap, classId, resolvedId, "constructor-param");
                }
            }

            // Field types
            foreach (var field in unit.Fields)
            {
                string? resolvedId = ResolveTypeName(field.Type);
                if (resolvedId != null && resolvedId != classId)
                    AddEdge(edgeMap, classId, resolvedId, "field-type");
            }

            // Property types
            foreach (var prop in unit.Properties)
            {
                string? resolvedId = ResolveTypeName(prop.Type);
                if (resolvedId != null && resolvedId != classId)
                    AddEdge(edgeMap, classId, resolvedId, "property-type");
            }

            // Method return types and parameter types
            foreach (var method in unit.Methods.Concat(unit.Constructors))
            {
                if (method.ReturnType != "void")
                {
                    string? resolvedId = ResolveTypeName(method.ReturnType);
                    if (resolvedId != null && resolvedId != classId)
                        AddEdge(edgeMap, classId, resolvedId, "return-type");
                }

                foreach (var param in method.Params)
                {
                    string? resolvedId = ResolveTypeName(param.Type);
                    if (resolvedId != null && resolvedId != classId)
                        AddEdge(edgeMap, classId, resolvedId, "parameter-type");
                }
            }

            // Inter-class flow edges (method calls across classes)
            foreach (var method in unit.Methods.Concat(unit.Constructors))
            {
                foreach (var call in method.Calls)
                {
                    if (call.ResolvedMethodId == null) continue;
                    string? targetClassId = ExtractClassIdFromMethodId(call.ResolvedMethodId);
                    if (targetClassId != null && targetClassId != classId
                        && _classLookup.ContainsKey(targetClassId))
                        AddEdge(edgeMap, classId, targetClassId, "method-call");
                }
            }
        }

        // Also scan top-level inter-class FlowEdges
        foreach (var result in _results)
        {
            foreach (var edge in result.FlowGraph.FlowEdges)
            {
                if (edge.Kind != "inter-class") continue;
                string? fromClass = ExtractClassIdFromSymbolId(edge.From);
                string? toClass = ExtractClassIdFromSymbolId(edge.To);
                if (fromClass != null && toClass != null && fromClass != toClass
                    && _classLookup.ContainsKey(fromClass) && _classLookup.ContainsKey(toClass))
                    AddEdge(edgeMap, fromClass, toClass, "method-call");
            }
        }

        return edgeMap.Select(kv => new DependencyEdge
        {
            From = kv.Key.from,
            To = kv.Key.to,
            Kind = kv.Key.kind,
            Weight = kv.Value
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Step 3: Build per-class relationship summaries
    // ═══════════════════════════════════════════════════════════════════════════

    private List<ClassRelationship> BuildClassRelationships(List<DependencyEdge> classDeps)
    {
        // Build forward (dependsOn) and reverse (dependedOnBy) indexes
        var dependsOn = new Dictionary<string, List<DependencyRef>>();
        var dependedOnBy = new Dictionary<string, List<DependencyRef>>();

        foreach (var edge in classDeps)
        {
            if (!dependsOn.ContainsKey(edge.From))
                dependsOn[edge.From] = new List<DependencyRef>();
            dependsOn[edge.From].Add(new DependencyRef { ClassId = edge.To, Kind = edge.Kind });

            if (!dependedOnBy.ContainsKey(edge.To))
                dependedOnBy[edge.To] = new List<DependencyRef>();
            dependedOnBy[edge.To].Add(new DependencyRef { ClassId = edge.From, Kind = edge.Kind });
        }

        var relationships = new List<ClassRelationship>();

        foreach (var (classId, unit) in _classLookup)
        {
            var deps = dependsOn.GetValueOrDefault(classId) ?? new List<DependencyRef>();
            var dependents = dependedOnBy.GetValueOrDefault(classId) ?? new List<DependencyRef>();

            // Deduplicate dependsOn/dependedOnBy by classId (keep first kind)
            var dedupDeps = deps
                .GroupBy(d => d.ClassId)
                .Select(g => g.First())
                .ToList();
            var dedupDependents = dependents
                .GroupBy(d => d.ClassId)
                .Select(g => g.First())
                .ToList();

            // Extract constructor params with resolution
            var ctorParams = new List<ConstructorParam>();
            foreach (var ctor in unit.Constructors)
            {
                foreach (var param in ctor.Params)
                {
                    ctorParams.Add(new ConstructorParam
                    {
                        Name = param.Name,
                        Type = param.Type,
                        ResolvedClassId = ResolveTypeName(param.Type)
                    });
                }
            }

            // Separate base class from interfaces
            string? baseClass = null;
            var interfaces = new List<string>();
            if (unit.BaseTypes.Count > 0)
            {
                baseClass = FindFirstNonInterface(unit.BaseTypes);
                foreach (var bt in unit.BaseTypes)
                {
                    if (bt != baseClass)
                        interfaces.Add(bt);
                }
            }

            relationships.Add(new ClassRelationship
            {
                ClassId = classId,
                ClassName = unit.Name,
                Namespace = unit.Namespace,
                Kind = unit.Kind,
                DependsOn = dedupDeps,
                DependedOnBy = dedupDependents,
                ConstructorParams = ctorParams,
                ImplementedInterfaces = interfaces,
                BaseClass = baseClass,
                FanIn = dedupDependents.Select(d => d.ClassId).Distinct().Count(),
                FanOut = dedupDeps.Select(d => d.ClassId).Distinct().Count()
            });
        }

        return relationships;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Aggregation helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static List<DependencyEdge> AggregateToNamespaceLevel(List<DependencyEdge> classDeps)
    {
        var nsEdgeMap = new Dictionary<(string from, string to, string kind), int>();

        foreach (var edge in classDeps)
        {
            string fromNs = ExtractNamespace(edge.From);
            string toNs = ExtractNamespace(edge.To);
            if (fromNs == toNs) continue; // skip intra-namespace edges

            var key = (fromNs, toNs, edge.Kind);
            nsEdgeMap[key] = nsEdgeMap.GetValueOrDefault(key) + edge.Weight;
        }

        return nsEdgeMap.Select(kv => new DependencyEdge
        {
            From = kv.Key.from,
            To = kv.Key.to,
            Kind = kv.Key.kind,
            Weight = kv.Value
        }).ToList();
    }

    private List<DependencyEdge> AggregateToProjectLevel(List<DependencyEdge> classDeps)
    {
        var projEdgeMap = new Dictionary<(string from, string to, string kind), int>();

        foreach (var edge in classDeps)
        {
            string fromProj = ExtractProjectName(edge.From);
            string toProj = ExtractProjectName(edge.To);
            if (fromProj == toProj) continue; // skip intra-project edges

            var key = (fromProj, toProj, edge.Kind);
            projEdgeMap[key] = projEdgeMap.GetValueOrDefault(key) + edge.Weight;
        }

        return projEdgeMap.Select(kv => new DependencyEdge
        {
            From = kv.Key.from,
            To = kv.Key.to,
            Kind = kv.Key.kind,
            Weight = kv.Value
        }).ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Type resolution helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private string? ResolveTypeName(string typeName)
    {
        // Strip generic type arguments: "ILogger<OrderServices>" → "ILogger"
        string baseName = StripGenericArgs(typeName);

        // Try exact match first
        if (_typeNameToClassId.TryGetValue(baseName, out string? classId))
            return classId;

        // Try without leading "I" for interface → implementation resolution
        // e.g., "IOrderRepository" → look for "OrderRepository"
        if (baseName.Length > 1 && baseName.StartsWith("I") && char.IsUpper(baseName[1]))
        {
            string implName = baseName.Substring(1);
            if (_typeNameToClassId.TryGetValue(implName, out classId))
                return classId;
        }

        return null;
    }

    private static string StripGenericArgs(string typeName)
    {
        int idx = typeName.IndexOf('<');
        return idx >= 0 ? typeName.Substring(0, idx) : typeName;
    }

    /// <summary>
    /// Heuristic: the first base type that doesn't start with "I" followed by uppercase
    /// is likely the base class. Everything else is an interface.
    /// This works for the common C# convention where interfaces are named IFoo.
    /// </summary>
    private static string? FindFirstNonInterface(List<string> baseTypes)
    {
        foreach (var bt in baseTypes)
        {
            string name = StripGenericArgs(bt);
            // Extract simple name from qualified name
            int dotIdx = name.LastIndexOf('.');
            string simpleName = dotIdx >= 0 ? name.Substring(dotIdx + 1) : name;

            if (simpleName.Length > 1 && simpleName.StartsWith("I") && char.IsUpper(simpleName[1]))
                continue; // likely an interface
            if (simpleName == "Enum" || simpleName == "System.Enum")
                continue; // skip Enum base
            return bt;
        }
        return null;
    }

    /// <summary>
    /// Extracts the class ID portion from a method/symbol ID.
    /// "Namespace.Class::Method[0]" → "Namespace.Class"
    /// "Namespace.Class::field:Name" → "Namespace.Class"
    /// </summary>
    private static string? ExtractClassIdFromMethodId(string methodId)
    {
        int idx = methodId.IndexOf("::");
        return idx > 0 ? methodId.Substring(0, idx) : null;
    }

    private static string? ExtractClassIdFromSymbolId(string symbolId)
    {
        // For method-scoped symbols: "Class::Method[0]/param:x" → "Class"
        int slashIdx = symbolId.IndexOf('/');
        string methodPart = slashIdx >= 0 ? symbolId.Substring(0, slashIdx) : symbolId;
        return ExtractClassIdFromMethodId(methodPart) ?? methodPart;
    }

    /// <summary>
    /// Extracts namespace from a class ID.
    /// "Namespace.SubNs.ClassName" → "Namespace.SubNs"
    /// "ClassName" → "(root)"
    /// </summary>
    private static string ExtractNamespace(string classId)
    {
        // Class IDs use the format "Namespace.ClassName" where the last segment is the class
        int lastDot = classId.LastIndexOf('.');
        return lastDot > 0 ? classId.Substring(0, lastDot) : "(root)";
    }

    /// <summary>
    /// Extracts project name from the source file path for a given class.
    /// Falls back to namespace-based grouping if source path is unavailable.
    /// </summary>
    private string ExtractProjectName(string classId)
    {
        if (_classToSource.TryGetValue(classId, out string? source))
        {
            // Try to extract project folder name from path
            // e.g., "src/Ordering.API/Services/OrderService.cs" → "Ordering.API"
            string? dir = Path.GetDirectoryName(source);
            while (dir != null)
            {
                string dirName = Path.GetFileName(dir);
                if (dirName.Contains('.') || dirName.EndsWith("API") || dirName.EndsWith("Core")
                    || dirName.EndsWith("Domain") || dirName.EndsWith("Infrastructure"))
                    return dirName;
                dir = Path.GetDirectoryName(dir);
            }
            return Path.GetFileName(Path.GetDirectoryName(source)) ?? "(unknown)";
        }

        // Fallback: use the first namespace segment
        string ns = ExtractNamespace(classId);
        int firstDot = ns.IndexOf('.');
        return firstDot > 0 ? ns.Substring(0, firstDot) : ns;
    }

    private static void AddEdge(Dictionary<(string from, string to, string kind), int> map,
        string from, string to, string kind)
    {
        var key = (from, to, kind);
        map[key] = map.GetValueOrDefault(key) + 1;
    }
}
