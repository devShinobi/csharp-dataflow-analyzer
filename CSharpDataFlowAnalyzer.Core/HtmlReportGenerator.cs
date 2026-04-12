using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Generates a self-contained HTML onboarding report from an <see cref="OnboardingOutput"/>.
/// The HTML skeleton (CSS, JS, layout) lives in the embedded resource ReportTemplate.html;
/// this class fills in the dynamic content via {{PLACEHOLDER}} substitution.
/// </summary>
public static class HtmlReportGenerator
{
    // Cached template loaded from the embedded resource once per process.
    private static string? _templateCache;

    private static string LoadTemplate()
    {
        if (_templateCache != null) return _templateCache;

        var asm = typeof(HtmlReportGenerator).Assembly;
        // The embedded resource name is: <RootNamespace>.<filename>
        const string ResourceName = "CSharpDataFlowAnalyzer.ReportTemplate.html";

        using var stream = asm.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded resource '{ResourceName}' not found. " +
                $"Available: {string.Join(", ", asm.GetManifestResourceNames())}");

        using var reader = new StreamReader(stream);
        _templateCache = reader.ReadToEnd();
        return _templateCache;
    }

    // ═══════════════════════════════════════════════════════════════════════════

    public static string Generate(OnboardingOutput data)
    {
        var template = LoadTemplate();

        var dg = data.DependencyGraph;
        int classCount = data.ClassRelationships.Count;
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm UTC");

        return template
            .Replace("{{CLASS_COUNT}}", classCount.ToString())
            .Replace("{{TIMESTAMP}}", timestamp)
            .Replace("{{FILE_COUNT}}", data.Results.Count.ToString())
            .Replace("{{STAT_CLASSES}}", classCount.ToString())
            .Replace("{{STAT_ENTRY_POINTS}}", data.EntryPoints.Count.ToString())
            .Replace("{{STAT_CLASS_DEPS}}", dg.ClassDependencies.Count.ToString())
            .Replace("{{STAT_NS_DEPS}}", dg.NamespaceDependencies.Count.ToString())
            .Replace("{{STAT_PROJECT_DEPS}}", dg.ProjectDependencies.Count.ToString())
            .Replace("{{ENTRY_POINTS_HTML}}", BuildEntryPointsHtml(data.EntryPoints))
            .Replace("{{GRAPH_DATA_JSON}}", BuildGraphJson(data))
            .Replace("{{HOT_NODES_HTML}}", BuildHotNodesHtml(data.HotNodes))
            .Replace("{{NAMESPACE_DEPS_HTML}}", BuildNamespaceDepsHtml(dg))
            .Replace("{{CLASS_INDEX_HTML}}", BuildClassIndexHtml(data.ClassRelationships, data.ClassExplanations))
            .Replace("{{CLASS_EXPLANATION_HTML}}", data.ClassExplanation != null
                ? BuildClassExplanationHtml(data.ClassExplanation)
                : string.Empty);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Section builders — each returns an HTML string
    // ═══════════════════════════════════════════════════════════════════════════

    private static string BuildEntryPointsHtml(List<EntryPoint> entryPoints)
    {
        if (entryPoints.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<section id=\"entry-points\">");
        sb.AppendLine("<h2>Entry Points</h2>");

        var grouped = entryPoints.GroupBy(e => e.Kind).OrderBy(g => g.Key);
        foreach (var group in grouped)
        {
            sb.AppendLine($"<h3>{Esc(FormatKind(group.Key))} ({group.Count()})</h3>");
            sb.AppendLine("<ul>");
            foreach (var ep in group)
            {
                string label = ep.MethodId != null
                    ? $"<a href=\"#{Esc(ep.ClassId)}\">{Esc(ep.ClassId)}</a> → {Esc(ep.MethodId)}"
                    : $"<a href=\"#{Esc(ep.ClassId)}\">{Esc(ep.ClassId)}</a>";
                if (ep.HttpMethod != null)
                    label += $" <span class=\"badge\">{Esc(ep.HttpMethod)}</span>";
                if (ep.Route != null)
                    label += $" <code>{Esc(ep.Route)}</code>";
                sb.AppendLine($"<li>{label}</li>");
            }
            sb.AppendLine("</ul>");
        }
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string BuildHotNodesHtml(List<HotNode> hotNodes)
    {
        if (hotNodes.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<section id=\"hot-nodes\">");
        sb.AppendLine("<h2>Hot Nodes (Most Connected)</h2>");
        sb.AppendLine("<table><thead><tr>");
        sb.AppendLine("<th>#</th><th>Class</th><th>Fan-In</th><th>Fan-Out</th><th>Total</th><th>Role</th>");
        sb.AppendLine("</tr></thead><tbody>");

        foreach (var node in hotNodes)
        {
            string roleClass = $"role-{node.Role}";
            sb.AppendLine($"<tr><td>{node.Rank}</td>");
            sb.AppendLine($"<td><a href=\"#{Esc(node.ClassId)}\">{Esc(node.ClassName)}</a></td>");
            sb.AppendLine($"<td>{node.FanIn}</td><td>{node.FanOut}</td><td>{node.TotalConnections}</td>");
            sb.AppendLine($"<td><span class=\"badge {roleClass}\">{Esc(node.Role)}</span></td></tr>");
        }

        sb.AppendLine("</tbody></table></section>");
        return sb.ToString();
    }

    private static string BuildNamespaceDepsHtml(DependencyGraph graph)
    {
        if (graph.NamespaceDependencies.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<section id=\"architecture\">");
        sb.AppendLine("<h2>Architecture Map (Namespace Dependencies)</h2>");

        var grouped = graph.NamespaceDependencies
            .GroupBy(e => e.From)
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            sb.AppendLine($"<details class=\"ns-group\"><summary><strong>{Esc(group.Key)}</strong> → {group.Count()} namespace(s)</summary>");
            sb.AppendLine("<ul>");
            foreach (var edge in group.OrderBy(e => e.To))
            {
                sb.AppendLine($"<li>→ {Esc(edge.To)} <span class=\"badge\">{Esc(edge.Kind)}</span> ×{edge.Weight}</li>");
            }
            sb.AppendLine("</ul></details>");
        }
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string BuildClassIndexHtml(List<ClassRelationship> classes,
        Dictionary<string, ClassExplanation> explanations)
    {
        if (classes.Count == 0) return string.Empty;

        var sb = new StringBuilder();
        sb.AppendLine("<section id=\"class-index\">");
        sb.AppendLine("<h2>Class Index</h2>");

        var grouped = classes
            .GroupBy(c => c.Namespace ?? "(root)")
            .OrderBy(g => g.Key);

        foreach (var nsGroup in grouped)
        {
            sb.AppendLine($"<details class=\"ns-section\" open><summary><strong>{Esc(nsGroup.Key)}</strong> ({nsGroup.Count()} classes)</summary>");

            foreach (var cls in nsGroup.OrderBy(c => c.ClassName))
            {
                explanations.TryGetValue(cls.ClassId, out var exp);

                sb.AppendLine($"<details class=\"class-detail\" id=\"{Esc(cls.ClassId)}\">");
                sb.AppendLine($"<summary>{Esc(cls.ClassName)} <span class=\"badge\">{Esc(cls.Kind)}</span>");
                sb.AppendLine($"<span class=\"fan\">↓{cls.FanIn} ↑{cls.FanOut}</span></summary>");
                sb.AppendLine("<div class=\"class-body\">");

                if (exp?.Responsibilities.Count > 0)
                {
                    sb.AppendLine("<p><strong>Responsibilities:</strong></p><ul class=\"responsibilities\">");
                    foreach (var r in exp.Responsibilities)
                        sb.AppendLine($"<li>{Esc(r)}</li>");
                    sb.AppendLine("</ul>");
                }

                if (cls.BaseClass != null)
                    sb.AppendLine($"<p><strong>Extends:</strong> {Esc(cls.BaseClass)}</p>");
                if (cls.ImplementedInterfaces.Count > 0)
                    sb.AppendLine($"<p><strong>Implements:</strong> {Esc(string.Join(", ", cls.ImplementedInterfaces))}</p>");

                if (cls.ConstructorParams.Count > 0)
                {
                    sb.AppendLine("<p><strong>Constructor (DI):</strong></p><ul>");
                    foreach (var p in cls.ConstructorParams)
                    {
                        string resolved = p.ResolvedClassId != null
                            ? $" → <a href=\"#{Esc(p.ResolvedClassId)}\">{Esc(p.ResolvedClassId)}</a>"
                            : "";
                        sb.AppendLine($"<li><code>{Esc(p.Type)}</code> {Esc(p.Name)}{resolved}</li>");
                    }
                    sb.AppendLine("</ul>");
                }

                if (exp?.PublicApi.Count > 0)
                {
                    sb.AppendLine("<p><strong>Public API:</strong></p>");
                    sb.AppendLine("<table><thead><tr><th>Method</th><th>Return</th><th>Params</th><th>Calls</th><th>Async</th></tr></thead><tbody>");
                    foreach (var m in exp.PublicApi)
                    {
                        sb.AppendLine($"<tr><td>{Esc(m.Name)}</td><td><code>{Esc(m.ReturnType)}</code></td>");
                        sb.AppendLine($"<td>{m.ParamCount}</td><td>{m.CallsOut}</td><td>{(m.IsAsync ? "✓" : "")}</td></tr>");
                    }
                    sb.AppendLine("</tbody></table>");
                }

                if (exp?.KeyFlows.Count > 0)
                {
                    sb.AppendLine("<p><strong>Outbound Call Chains:</strong></p><ul class=\"key-flows\">");
                    foreach (var flow in exp.KeyFlows)
                        sb.AppendLine($"<li><code>{Esc(flow.Description)}</code></li>");
                    sb.AppendLine("</ul>");
                }

                if (exp != null)
                {
                    if (exp.State.MutableFields.Count > 0)
                    {
                        sb.AppendLine("<p><strong>Mutable state:</strong></p><ul>");
                        foreach (var f in exp.State.MutableFields)
                            sb.AppendLine($"<li><code>{Esc(f)}</code></li>");
                        sb.AppendLine("</ul>");
                    }
                    if (exp.State.ReadonlyFields.Count > 0)
                    {
                        sb.AppendLine("<p><strong>Readonly fields:</strong></p><ul>");
                        foreach (var f in exp.State.ReadonlyFields)
                            sb.AppendLine($"<li><code>{Esc(f)}</code></li>");
                        sb.AppendLine("</ul>");
                    }
                }

                if (cls.DependsOn.Count > 0)
                {
                    sb.AppendLine("<p><strong>Depends on:</strong></p><ul>");
                    foreach (var dep in cls.DependsOn)
                        sb.AppendLine($"<li><a href=\"#{Esc(dep.ClassId)}\">{Esc(dep.ClassId)}</a> <span class=\"badge\">{Esc(dep.Kind)}</span></li>");
                    sb.AppendLine("</ul>");
                }

                if (cls.DependedOnBy.Count > 0)
                {
                    sb.AppendLine("<p><strong>Used by:</strong></p><ul>");
                    foreach (var dep in cls.DependedOnBy)
                        sb.AppendLine($"<li><a href=\"#{Esc(dep.ClassId)}\">{Esc(dep.ClassId)}</a> <span class=\"badge\">{Esc(dep.Kind)}</span></li>");
                    sb.AppendLine("</ul>");
                }

                sb.AppendLine("</div></details>");
            }

            sb.AppendLine("</details>");
        }
        sb.AppendLine("</section>");
        return sb.ToString();
    }

    private static string BuildClassExplanationHtml(ClassExplanation exp)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<section id=\"explanation\">");
        sb.AppendLine($"<h2>Class Explanation: {Esc(exp.ClassName)}</h2>");

        if (exp.Responsibilities.Count > 0)
        {
            sb.AppendLine("<h3>Responsibilities</h3><ul>");
            foreach (var r in exp.Responsibilities)
                sb.AppendLine($"<li>{Esc(r)}</li>");
            sb.AppendLine("</ul>");
        }

        if (exp.IsEntryPoint)
            sb.AppendLine("<p class=\"badge role-hub\">Entry Point</p>");
        if (exp.HotRank != null)
            sb.AppendLine($"<p>Hot rank: <strong>#{exp.HotRank}</strong></p>");

        sb.AppendLine("<h3>State</h3>");
        if (!exp.State.HasMutableState)
        {
            sb.AppendLine("<p>Immutable (no mutable fields or settable properties)</p>");
        }
        else
        {
            if (exp.State.MutableFields.Count > 0)
            {
                sb.AppendLine("<p><strong>Mutable fields:</strong></p><ul>");
                foreach (var f in exp.State.MutableFields)
                    sb.AppendLine($"<li><code>{Esc(f)}</code></li>");
                sb.AppendLine("</ul>");
            }
        }
        if (exp.State.ReadonlyFields.Count > 0)
        {
            sb.AppendLine("<p><strong>Readonly fields:</strong></p><ul>");
            foreach (var f in exp.State.ReadonlyFields)
                sb.AppendLine($"<li><code>{Esc(f)}</code></li>");
            sb.AppendLine("</ul>");
        }

        if (exp.PublicApi.Count > 0)
        {
            sb.AppendLine("<h3>Public API</h3><table><thead><tr>");
            sb.AppendLine("<th>Method</th><th>Return</th><th>Params</th><th>Calls</th><th>Async</th>");
            sb.AppendLine("</tr></thead><tbody>");
            foreach (var m in exp.PublicApi)
            {
                sb.AppendLine($"<tr><td>{Esc(m.Name)}</td><td><code>{Esc(m.ReturnType)}</code></td>");
                sb.AppendLine($"<td>{m.ParamCount}</td><td>{m.CallsOut}</td>");
                sb.AppendLine($"<td>{(m.IsAsync ? "✓" : "")}</td></tr>");
            }
            sb.AppendLine("</tbody></table>");
        }

        if (exp.KeyFlows.Count > 0)
        {
            sb.AppendLine("<h3>Outbound Call Chains</h3><ul>");
            foreach (var flow in exp.KeyFlows)
                sb.AppendLine($"<li><code>{Esc(flow.Description)}</code></li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</section>");
        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Graph JSON builder
    // ═══════════════════════════════════════════════════════════════════════════

    private static string BuildGraphJson(OnboardingOutput data)
    {
        const int MaxNodes = 150;
        const int MaxEdges = 700;

        var topClasses = data.ClassRelationships
            .OrderByDescending(r => r.FanIn + r.FanOut)
            .Take(MaxNodes)
            .ToList();
        var topIds = new HashSet<string>(topClasses.Select(c => c.ClassId));
        var hotMap = data.HotNodes.ToDictionary(h => h.ClassId, h => h);
        var entrySet = new HashSet<string>(data.EntryPoints.Select(e => e.ClassId));

        var nodesJson = "[" + string.Join(",", topClasses.Select(c =>
        {
            hotMap.TryGetValue(c.ClassId, out var hot);
            return "{" +
                $"\"id\":{JStr(c.ClassId)}," +
                $"\"name\":{JStr(c.ClassName)}," +
                $"\"ns\":{JStr(c.Namespace ?? "")}," +
                $"\"role\":{JStr(hot?.Role ?? "leaf")}," +
                $"\"fanIn\":{c.FanIn}," +
                $"\"fanOut\":{c.FanOut}," +
                $"\"isEntryPoint\":{(entrySet.Contains(c.ClassId) ? "true" : "false")}," +
                $"\"hotRank\":{(hot != null ? hot.Rank.ToString() : "null")}" +
                "}";
        })) + "]";

        var edgesJson = "[" + string.Join(",", data.DependencyGraph.ClassDependencies
            .Where(e => topIds.Contains(e.From) && topIds.Contains(e.To))
            .Take(MaxEdges)
            .Select(e => $"{{\"from\":{JStr(e.From)},\"to\":{JStr(e.To)},\"kind\":{JStr(e.Kind)}}}")) + "]";

        var flowParts = new List<string>();
        foreach (var (classId, exp) in data.ClassExplanations)
        {
            if (!topIds.Contains(classId)) continue;
            foreach (var flow in exp.KeyFlows)
            {
                var chain = "[" + string.Join(",", flow.MethodChain.Select(JStr)) + "]";
                flowParts.Add($"{{\"classId\":{JStr(classId)},\"desc\":{JStr(flow.Description)},\"chain\":{chain}}}");
            }
        }

        return $"{{\"nodes\":{nodesJson},\"edges\":{edgesJson},\"flows\":[{string.Join(",", flowParts)}]}}";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    // HTML escaping
    private static string Esc(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // JSON string escaping (not HTML escaping)
    private static string JStr(string s) =>
        "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";

    private static string FormatKind(string kind) => kind switch
    {
        "main" => "Main Entry Points",
        "controller-action" => "ASP.NET Controller Actions",
        "minimal-api" => "Minimal API Endpoints",
        "background-service" => "Background Services",
        "mediatr-handler" => "MediatR Handlers",
        _ => kind
    };
}
