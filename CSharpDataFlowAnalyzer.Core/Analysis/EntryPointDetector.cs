using System;
using System.Collections.Generic;
using System.Linq;

namespace CSharpDataFlowAnalyzer.Analysis;

/// <summary>
/// Detects application entry points from AnalysisResult data using heuristics
/// based on class attributes, base types, method names, and call patterns.
/// </summary>
internal sealed class EntryPointDetector
{
    // ASP.NET controller base type names (short and qualified)
    private static readonly HashSet<string> ControllerBaseTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Controller", "ControllerBase",
        "Microsoft.AspNetCore.Mvc.Controller",
        "Microsoft.AspNetCore.Mvc.ControllerBase"
    };

    // ASP.NET HTTP method attributes
    private static readonly HashSet<string> HttpMethodAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        "HttpGet", "HttpPost", "HttpPut", "HttpDelete", "HttpPatch", "HttpHead", "HttpOptions",
        "Microsoft.AspNetCore.Mvc.HttpGetAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPostAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPutAttribute",
        "Microsoft.AspNetCore.Mvc.HttpDeleteAttribute",
        "Microsoft.AspNetCore.Mvc.HttpPatchAttribute"
    };

    // Background service base types
    private static readonly HashSet<string> BackgroundServiceTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BackgroundService", "IHostedService",
        "Microsoft.Extensions.Hosting.BackgroundService",
        "Microsoft.Extensions.Hosting.IHostedService"
    };

    // Minimal API mapping methods
    private static readonly HashSet<string> MinimalApiMapMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "MapGet", "MapPost", "MapPut", "MapDelete", "MapPatch",
        "MapGroup", "MapFallback"
    };

    // MediatR handler interfaces
    private static readonly string[] MediatRHandlerPrefixes =
    {
        "IRequestHandler<", "INotificationHandler<",
        "MediatR.IRequestHandler<", "MediatR.INotificationHandler<"
    };

    public static List<EntryPoint> Detect(
        List<AnalysisResult> results,
        Action<string>? log = null)
    {
        var entryPoints = new List<EntryPoint>();

        foreach (var result in results)
        {
            foreach (var unit in result.FlowGraph.Units)
            {
                DetectMain(unit, entryPoints);
                DetectControllerActions(unit, entryPoints);
                DetectMinimalApiEndpoints(unit, entryPoints);
                DetectBackgroundServices(unit, entryPoints);
                DetectMediatRHandlers(unit, entryPoints);
            }
        }

        log?.Invoke($"  Entry points: {entryPoints.Count} detected " +
                     $"({entryPoints.GroupBy(e => e.Kind).Select(g => $"{g.Count()} {g.Key}").DefaultIfEmpty("none").Aggregate((a, b) => $"{a}, {b}")})");

        return entryPoints;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Detection heuristics
    // ═══════════════════════════════════════════════════════════════════════════

    private static void DetectMain(ClassUnit unit, List<EntryPoint> results)
    {
        foreach (var method in unit.Methods)
        {
            if (method.Name is "Main" or "<Main>$")
            {
                results.Add(new EntryPoint
                {
                    ClassId = unit.Id,
                    MethodId = method.Id,
                    Kind = "main"
                });
            }
        }
    }

    private static void DetectControllerActions(ClassUnit unit, List<EntryPoint> results)
    {
        bool isController = HasAnyBaseType(unit, ControllerBaseTypes)
            || HasAttribute(unit.Attributes, "ApiController")
            || HasAttribute(unit.Attributes, "Microsoft.AspNetCore.Mvc.ApiControllerAttribute");

        if (!isController) return;

        foreach (var method in unit.Methods)
        {
            if (method.Accessibility != "public") continue;

            string? httpMethod = DetectHttpMethod(method);
            string? route = DetectRoute(method);

            results.Add(new EntryPoint
            {
                ClassId = unit.Id,
                MethodId = method.Id,
                Kind = "controller-action",
                HttpMethod = httpMethod,
                Route = route
            });
        }
    }

    private static void DetectMinimalApiEndpoints(ClassUnit unit, List<EntryPoint> results)
    {
        // Scan all methods for calls to MapGet/MapPost/etc.
        foreach (var method in unit.Methods.Concat(unit.Constructors))
        {
            foreach (var call in method.Calls)
            {
                if (!MinimalApiMapMethods.Contains(call.MethodName)) continue;

                // Extract route from first argument
                string? route = call.Arguments.Count > 0
                    ? StripQuotes(call.Arguments[0].Expression)
                    : null;

                // Extract HTTP method from the Map* method name
                string httpMethod = call.MethodName.Replace("Map", "").ToUpperInvariant();
                if (httpMethod is "GROUP" or "FALLBACK")
                    httpMethod = call.MethodName; // keep as-is for non-HTTP methods

                results.Add(new EntryPoint
                {
                    ClassId = unit.Id,
                    MethodId = method.Id,
                    Kind = "minimal-api",
                    HttpMethod = httpMethod,
                    Route = route
                });
            }
        }
    }

    private static void DetectBackgroundServices(ClassUnit unit, List<EntryPoint> results)
    {
        if (!HasAnyBaseType(unit, BackgroundServiceTypes)) return;

        // Find ExecuteAsync or StartAsync
        var execMethod = unit.Methods.FirstOrDefault(m =>
            m.Name is "ExecuteAsync" or "StartAsync");

        results.Add(new EntryPoint
        {
            ClassId = unit.Id,
            MethodId = execMethod?.Id,
            Kind = "background-service"
        });
    }

    private static void DetectMediatRHandlers(ClassUnit unit, List<EntryPoint> results)
    {
        bool isHandler = unit.BaseTypes.Any(bt =>
            MediatRHandlerPrefixes.Any(prefix => bt.Contains(prefix, StringComparison.OrdinalIgnoreCase)));

        if (!isHandler) return;

        var handleMethod = unit.Methods.FirstOrDefault(m => m.Name == "Handle");

        results.Add(new EntryPoint
        {
            ClassId = unit.Id,
            MethodId = handleMethod?.Id,
            Kind = "mediatr-handler"
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static bool HasAnyBaseType(ClassUnit unit, HashSet<string> candidates)
    {
        return unit.BaseTypes.Any(bt =>
        {
            string stripped = StripGenericArgs(bt);
            // Check full name and simple name
            if (candidates.Contains(stripped)) return true;
            int dotIdx = stripped.LastIndexOf('.');
            return dotIdx >= 0 && candidates.Contains(stripped.Substring(dotIdx + 1));
        });
    }

    private static bool HasAttribute(List<string>? attributes, string name)
    {
        if (attributes == null) return false;
        return attributes.Any(a =>
        {
            // Match "ApiController" or "Microsoft.AspNetCore.Mvc.ApiControllerAttribute"
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.EndsWith("Attribute", StringComparison.OrdinalIgnoreCase))
            {
                string withoutSuffix = a.Substring(0, a.Length - "Attribute".Length);
                if (withoutSuffix.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
                int dotIdx = withoutSuffix.LastIndexOf('.');
                if (dotIdx >= 0)
                    return withoutSuffix.Substring(dotIdx + 1).Equals(name, StringComparison.OrdinalIgnoreCase);
            }
            int lastDot = a.LastIndexOf('.');
            return lastDot >= 0 && a.Substring(lastDot + 1).Equals(name, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string? DetectHttpMethod(MethodNode method)
    {
        if (method.Attributes == null) return null;
        foreach (var attr in method.Attributes)
        {
            string simple = StripNamespace(attr).Replace("Attribute", "");
            if (simple.StartsWith("Http", StringComparison.OrdinalIgnoreCase))
                return simple.Substring(4).ToUpperInvariant(); // "HttpGet" → "GET"
        }
        return null;
    }

    private static string? DetectRoute(MethodNode method)
    {
        if (method.Attributes == null) return null;
        foreach (var attr in method.Attributes)
        {
            if (attr.Contains("Route", StringComparison.OrdinalIgnoreCase))
            {
                // Try to extract route value — attributes are stored as display strings,
                // route would be in the attribute constructor arg if captured.
                // For now, we just note the presence of a route attribute.
                return null; // TODO: extract route template when attribute args are captured
            }
        }
        return null;
    }

    private static string StripGenericArgs(string typeName)
    {
        int idx = typeName.IndexOf('<');
        return idx >= 0 ? typeName.Substring(0, idx) : typeName;
    }

    private static string StripNamespace(string qualifiedName)
    {
        int lastDot = qualifiedName.LastIndexOf('.');
        return lastDot >= 0 ? qualifiedName.Substring(lastDot + 1) : qualifiedName;
    }

    private static string? StripQuotes(string expr)
    {
        expr = expr.Trim();
        if (expr.Length >= 2 && expr[0] == '"' && expr[^1] == '"')
            return expr.Substring(1, expr.Length - 2);
        return expr;
    }
}
