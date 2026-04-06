using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpDataFlowAnalyzer.Analysis;

/// <summary>
/// Produces a structured explanation of a single class by combining
/// dependency, entry point, hot path, and flow graph data.
/// </summary>
internal sealed class ClassExplainer
{
    public static ClassExplanation? Explain(
        string classId,
        List<AnalysisResult> results,
        List<ClassRelationship> relationships,
        List<EntryPoint> entryPoints,
        List<HotNode> hotNodes,
        Action<string>? log = null)
    {
        // Find the class unit across all results
        ClassUnit? unit = null;
        foreach (var result in results)
        {
            unit = result.FlowGraph.Units.FirstOrDefault(u => u.Id == classId);
            if (unit != null) break;
        }

        if (unit == null)
        {
            // Try partial match (class name without namespace)
            foreach (var result in results)
            {
                unit = result.FlowGraph.Units.FirstOrDefault(u =>
                    u.Name.Equals(classId, StringComparison.OrdinalIgnoreCase)
                    || u.Id.EndsWith("." + classId, StringComparison.OrdinalIgnoreCase));
                if (unit != null)
                {
                    classId = unit.Id;
                    break;
                }
            }
        }

        if (unit == null)
        {
            log?.Invoke($"  Warning: class '{classId}' not found in analyzed results.");
            return null;
        }

        var relationship = relationships.FirstOrDefault(r => r.ClassId == classId)
                           ?? new ClassRelationship { ClassId = classId, ClassName = unit.Name };
        var isEntryPoint = entryPoints.Any(e => e.ClassId == classId);
        var hotNode = hotNodes.FirstOrDefault(h => h.ClassId == classId);

        var explanation = new ClassExplanation
        {
            ClassId = classId,
            ClassName = unit.Name,
            Kind = unit.Kind,
            Dependencies = relationship,
            State = BuildState(unit),
            PublicApi = BuildPublicApi(unit),
            KeyFlows = BuildKeyFlows(unit, results),
            Responsibilities = InferResponsibilities(unit, relationship, entryPoints, isEntryPoint),
            IsEntryPoint = isEntryPoint,
            HotRank = hotNode?.Rank
        };

        log?.Invoke($"  Explained: {unit.Name} — {explanation.Responsibilities.Count} responsibilities, " +
                     $"{explanation.PublicApi.Count} public methods, {explanation.KeyFlows.Count} key flows");

        return explanation;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // State analysis
    // ═══════════════════════════════════════════════════════════════════════════

    private static ClassState BuildState(ClassUnit unit)
    {
        var mutableFields = unit.Fields
            .Where(f => !f.IsReadonly && !f.IsStatic)
            .Select(f => $"{f.Type} {f.Name}")
            .ToList();

        var readonlyFields = unit.Fields
            .Where(f => f.IsReadonly || f.IsStatic)
            .Select(f => $"{f.Type} {f.Name}")
            .ToList();

        var properties = unit.Properties
            .Select(p => $"{p.Type} {p.Name}" + (p.HasSetter ? " (read-write)" : " (read-only)"))
            .ToList();

        return new ClassState
        {
            MutableFields = mutableFields,
            ReadonlyFields = readonlyFields,
            Properties = properties,
            HasMutableState = mutableFields.Count > 0 || unit.Properties.Any(p => p.HasSetter)
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Public API summary
    // ═══════════════════════════════════════════════════════════════════════════

    private static List<MethodSummary> BuildPublicApi(ClassUnit unit)
    {
        return unit.Methods
            .Where(m => m.Accessibility == "public")
            .Select(m => new MethodSummary
            {
                MethodId = m.Id,
                Name = m.Name,
                ReturnType = m.ReturnType,
                ParamCount = m.Params.Count,
                CallsOut = m.Calls.Count,
                IsAsync = m.IsAsync
            })
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Key flow detection
    // ═══════════════════════════════════════════════════════════════════════════

    private static List<KeyFlow> BuildKeyFlows(ClassUnit unit, List<AnalysisResult> results)
    {
        var flows = new List<KeyFlow>();

        // For each public method, follow inter-method calls 2-3 hops deep
        foreach (var method in unit.Methods.Where(m => m.Accessibility == "public"))
        {
            var chain = new List<string> { method.Id };
            var description = $"{method.Name}(";
            if (method.Params.Count > 0)
                description += string.Join(", ", method.Params.Select(p => p.Type));
            description += ")";

            // Follow resolved method calls
            var visited = new HashSet<string> { method.Id };
            CollectCallChain(method, results, chain, visited, maxDepth: 3);

            if (chain.Count > 1)
            {
                description += " → " + string.Join(" → ", chain.Skip(1).Select(FormatMethodName));

                flows.Add(new KeyFlow
                {
                    Description = description,
                    MethodChain = chain
                });
            }
        }

        return flows;
    }

    private static void CollectCallChain(
        MethodNode method,
        List<AnalysisResult> results,
        List<string> chain,
        HashSet<string> visited,
        int maxDepth)
    {
        if (maxDepth <= 0) return;

        foreach (var call in method.Calls)
        {
            if (call.ResolvedMethodId == null) continue;
            if (!visited.Add(call.ResolvedMethodId)) continue;

            chain.Add(call.ResolvedMethodId);

            // Try to find the target method in results for deeper traversal
            var targetMethod = FindMethod(call.ResolvedMethodId, results);
            if (targetMethod != null)
                CollectCallChain(targetMethod, results, chain, visited, maxDepth - 1);

            break; // Only follow the first resolved call per method to keep flows simple
        }
    }

    private static MethodNode? FindMethod(string methodId, List<AnalysisResult> results)
    {
        foreach (var result in results)
        {
            foreach (var unit in result.FlowGraph.Units)
            {
                var method = unit.Methods.Concat(unit.Constructors)
                    .FirstOrDefault(m => m.Id == methodId);
                if (method != null) return method;
            }
        }
        return null;
    }

    private static string FormatMethodName(string methodId)
    {
        // "Namespace.Class::Method[0]" → "Class.Method"
        int colonIdx = methodId.IndexOf("::");
        if (colonIdx < 0) return methodId;

        string classId = methodId.Substring(0, colonIdx);
        string methodPart = methodId.Substring(colonIdx + 2);

        // Strip overload index
        int bracketIdx = methodPart.IndexOf('[');
        if (bracketIdx >= 0) methodPart = methodPart.Substring(0, bracketIdx);

        // Get simple class name
        int dotIdx = classId.LastIndexOf('.');
        string className = dotIdx >= 0 ? classId.Substring(dotIdx + 1) : classId;

        return $"{className}.{methodPart}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Responsibility inference
    // ═══════════════════════════════════════════════════════════════════════════

    private static List<string> InferResponsibilities(
        ClassUnit unit,
        ClassRelationship relationship,
        List<EntryPoint> entryPoints,
        bool isEntryPoint)
    {
        var responsibilities = new List<string>();

        // Entry point roles
        var classEntryPoints = entryPoints.Where(e => e.ClassId == unit.Id).ToList();
        foreach (var ep in classEntryPoints)
        {
            responsibilities.Add(ep.Kind switch
            {
                "main" => "Application entry point",
                "controller-action" => $"Handles HTTP requests ({ep.HttpMethod ?? "mixed methods"})",
                "minimal-api" => $"Defines minimal API endpoint ({ep.HttpMethod} {ep.Route ?? ""})",
                "background-service" => "Long-running background service",
                "mediatr-handler" => "Handles MediatR requests/notifications",
                _ => $"Entry point ({ep.Kind})"
            });
        }

        // DI dependencies
        if (relationship.ConstructorParams.Count > 0)
        {
            // Only count params that resolve to known classes (DI services), not primitives
            var serviceParams = relationship.ConstructorParams
                .Where(p => p.ResolvedClassId != null)
                .Select(p => StripGenericArgs(p.Type))
                .ToList();
            if (serviceParams.Count > 0)
                responsibilities.Add($"Depends on {serviceParams.Count} injected service(s): " +
                                      string.Join(", ", serviceParams));
        }

        // State management
        int mutableFieldCount = unit.Fields.Count(f => !f.IsReadonly && !f.IsStatic);
        if (mutableFieldCount > 0)
            responsibilities.Add($"Manages mutable state ({mutableFieldCount} field(s))");
        else if (unit.Fields.Count == 0 && unit.Properties.Count == 0
                 && unit.Methods.All(m => m.IsStatic))
            responsibilities.Add("Stateless utility/helper class");

        // Interface implementations
        if (relationship.ImplementedInterfaces.Count > 0)
            responsibilities.Add($"Implements {string.Join(", ", relationship.ImplementedInterfaces)}");

        // Data model detection
        if (unit.Kind is "record" or "record-struct" or "enum")
            responsibilities.Add($"Data model ({unit.Kind})");
        else if (unit.Fields.Count == 0 && unit.Properties.Count > 2 && unit.Methods.Count == 0)
            responsibilities.Add("Data transfer object (DTO)");

        // Disposable resource management
        if (unit.BaseTypes.Any(bt => bt.Contains("IDisposable")))
            responsibilities.Add("Manages disposable resources");

        return responsibilities;
    }

    private static string StripGenericArgs(string typeName)
    {
        int idx = typeName.IndexOf('<');
        return idx >= 0 ? typeName.Substring(0, idx) : typeName;
    }
}
