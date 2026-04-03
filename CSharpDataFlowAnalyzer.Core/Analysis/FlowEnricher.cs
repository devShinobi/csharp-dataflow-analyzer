using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Post-processing passes that enrich a FlowGraph after the primary walk:
///   1. LINQ chain decomposition — groups chained .Select().Where().ToList() calls
///   2. Interpolated string tagging — marks returns sourced from $"..." expressions
///   3. Conditional init tagging — marks locals with ternary / switch / ?? initializers
///   4. Property accessor flows — promotes assignment edges to property-write/read kinds
///   5. Object initializer decomposition — records which property gets which source
/// </summary>
public static class FlowEnricher
{
    public static void Enrich(FlowGraph graph, CSharpCompilation compilation)
    {
        foreach (var unit in graph.Units)
        {
            foreach (var method in unit.Methods.Concat(unit.Constructors))
            {
                EnrichLinqChains(method);
                EnrichInterpolatedStrings(method);
                EnrichConditionalFlow(method);
                EnrichPropertyFlows(unit, method);
                EnrichObjectInitializers(method);
            }
        }
    }

    // ── 1. LINQ chain decomposition ──────────────────────────────────────────
    // Identifies groups of calls that form a chain: src.Select(...).Where(...).ToList()
    // and records the ordering as a LinqChain on the method.

    private static readonly HashSet<string> LinqMethods = new(System.StringComparer.Ordinal)
    {
        "Select","Where","OrderBy","OrderByDescending","ThenBy","ThenByDescending",
        "GroupBy","Join","GroupJoin","SelectMany","Distinct","DistinctBy",
        "Take","TakeWhile","Skip","SkipWhile","First","FirstOrDefault",
        "Single","SingleOrDefault","Last","LastOrDefault","Any","All","Count",
        "Sum","Min","Max","Average","Aggregate","ToList","ToArray","ToDictionary",
        "ToHashSet","ToLookup","AsEnumerable","AsQueryable","Cast","OfType",
        "Zip","Append","Prepend","Reverse","Concat","Union","Intersect","Except"
    };

    private static void EnrichLinqChains(MethodNode method)
    {
        var linqCalls = method.Calls
            .Where(c => LinqMethods.Contains(c.MethodName))
            .ToList();

        if (linqCalls.Count < 2) return;

        // Build chains by following resultAssignedTo linkage and isChained flag
        // Strategy: group calls where expression B is a substring of expression A
        // (indicating B is the receiver of A in a chain).
        var chains = new List<LinqChain>();
        var assigned = new HashSet<string>();

        foreach (var terminal in linqCalls.Where(c => !c.IsChained || c.ResultAssignedTo != null))
        {
            if (assigned.Contains(terminal.Id)) continue;

            // Walk back: find all calls whose expression is a prefix of terminal.Expression
            var chain = linqCalls
                .Where(c => !assigned.Contains(c.Id) && terminal.Expression.Contains(c.MethodName))
                .OrderBy(c => c.Expression.Length)
                .ToList();

            if (chain.Count >= 2)
            {
                chains.Add(new LinqChain
                {
                    Steps = chain.Select((c, i) => new LinqChainStep
                    {
                        Order = i,
                        CallId = c.Id,
                        MethodName = c.MethodName
                    }).ToList()
                });
                foreach (var c in chain) assigned.Add(c.Id);
            }
        }

        if (chains.Any())
            method.LinqChains = chains;
    }

    // ── 2. Interpolated string tagging ───────────────────────────────────────

    private static void EnrichInterpolatedStrings(MethodNode method)
    {
        foreach (var ret in method.Returns)
            if (ret.Expression != null && ret.Expression.StartsWith("$\""))
                ret.IsInterpolated = true;
    }

    // ── 3. Conditional init tagging ──────────────────────────────────────────

    private static void EnrichConditionalFlow(MethodNode method)
    {
        foreach (var local in method.Locals)
        {
            if (local.InitExpression == null) continue;

            bool isTernary = local.InitExpression.Contains('?') && local.InitExpression.Contains(':');
            bool isSwitch  = local.InitExpression.Contains("switch");
            bool isNull    = local.InitExpression.Contains("??");

            if (isTernary || isSwitch || isNull)
            {
                local.HasConditionalInit = true;
                local.ConditionalKind = isSwitch ? "switch-expression"
                                      : isNull   ? "null-coalescing"
                                                 : "ternary";
            }
        }
    }

    // ── 4. Property accessor flows ───────────────────────────────────────────

    private static void EnrichPropertyFlows(ClassUnit unit, MethodNode method)
    {
        var propsByName = unit.Properties.ToDictionary(p => p.Name, p => p);

        // Tag assignments whose target is a property
        foreach (var assign in method.Assignments)
        {
            string targetBase = assign.Target.Split('.').Last().Trim();
            if (propsByName.ContainsKey(targetBase))
            {
                assign.TargetKind = "property";
                var existing = method.FlowEdges.FirstOrDefault(e => e.To == assign.TargetId);
                if (existing != null) existing.Kind = "property-write";
            }
        }

        // Tag locals whose init expression reads a property
        foreach (var local in method.Locals)
        {
            if (local.InitExpression == null) continue;
            foreach (var prop in unit.Properties)
            {
                if (local.InitExpression.Contains(prop.Name) && !local.DataSourceIds.Contains(prop.Id))
                {
                    local.DataSourceIds.Add(prop.Id);
                    method.FlowEdges.Add(new FlowEdge
                    {
                        From = prop.Id,
                        To = local.Id,
                        Kind = "property-read",
                        Label = prop.Name
                    });
                }
            }
        }
    }

    // ── 5. Object initializer decomposition ──────────────────────────────────
    // new Order { CustomerId = customer.Id, Total = total }
    // → ObjectInitAssignments: [{property:"CustomerId", valueExpression:"customer.Id"}, ...]

    private static void EnrichObjectInitializers(MethodNode method)
    {
        foreach (var local in method.Locals)
        {
            if (local.InitExpression == null) continue;

            int braceOpen = local.InitExpression.IndexOf('{');
            int braceClose = local.InitExpression.LastIndexOf('}');
            if (braceOpen < 0 || braceClose <= braceOpen) continue;

            string initBody = local.InitExpression.Substring(braceOpen + 1, braceClose - braceOpen - 1);
            var assignments = new List<ObjectInitAssignment>();

            // Split on commas that are NOT inside nested braces
            int depth = 0;
            int start = 0;
            var parts = new List<string>();
            for (int i = 0; i < initBody.Length; i++)
            {
                if (initBody[i] == '{') depth++;
                else if (initBody[i] == '}') depth--;
                else if (initBody[i] == ',' && depth == 0)
                {
                    parts.Add(initBody[start..i].Trim());
                    start = i + 1;
                }
            }
            if (start < initBody.Length) parts.Add(initBody[start..].Trim());

            foreach (var part in parts)
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                string propName = part[..eq].Trim();
                string valExpr  = part[(eq + 1)..].Trim();
                if (string.IsNullOrWhiteSpace(propName) || string.IsNullOrWhiteSpace(valExpr)) continue;
                // Skip if looks like == (comparison) 
                if (valExpr.StartsWith("=")) continue;

                assignments.Add(new ObjectInitAssignment
                {
                    Property = propName,
                    ValueExpression = valExpr
                });
            }

            if (assignments.Any())
                local.ObjectInitAssignments = assignments;
        }
    }
}
