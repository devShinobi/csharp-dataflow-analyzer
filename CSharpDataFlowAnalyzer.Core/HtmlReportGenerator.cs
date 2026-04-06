using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Generates a self-contained HTML onboarding report from an <see cref="OnboardingOutput"/>.
/// The output is a single HTML file with embedded CSS and minimal vanilla JS.
/// </summary>
public static class HtmlReportGenerator
{
    public static string Generate(OnboardingOutput data)
    {
        var sb = new StringBuilder(32_000);

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang=\"en\">");
        WriteHead(sb, data);
        sb.AppendLine("<body>");
        WriteHeader(sb, data);
        WriteSummary(sb, data);
        WriteEntryPoints(sb, data.EntryPoints);
        WriteHotNodes(sb, data.HotNodes);
        WriteNamespaceDeps(sb, data.DependencyGraph);
        WriteClassIndex(sb, data.ClassRelationships);

        if (data.ClassExplanation != null)
            WriteClassExplanation(sb, data.ClassExplanation);

        WriteScript(sb);
        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════════════

    private static void WriteHead(StringBuilder sb, OnboardingOutput data)
    {
        int classCount = data.ClassRelationships.Count;
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine($"<title>Codebase Report — {classCount} classes</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css);
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
    }

    private static void WriteHeader(StringBuilder sb, OnboardingOutput data)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm UTC");
        sb.AppendLine($"<header><h1>Codebase Onboarding Report</h1>");
        sb.AppendLine($"<p class=\"meta\">Generated {timestamp} &middot; {data.Results.Count} file(s) analyzed</p>");
        sb.AppendLine("<div class=\"toolbar\">");
        sb.AppendLine("<input type=\"text\" id=\"filter\" placeholder=\"Filter by namespace or class...\" />");
        sb.AppendLine("<button onclick=\"toggleAll()\">Expand / Collapse All</button>");
        sb.AppendLine("</div></header>");
    }

    private static void WriteSummary(StringBuilder sb, OnboardingOutput data)
    {
        var dg = data.DependencyGraph;
        sb.AppendLine("<section id=\"summary\">");
        sb.AppendLine("<h2>Summary</h2>");
        sb.AppendLine("<div class=\"stats\">");
        WriteStat(sb, "Classes", data.ClassRelationships.Count);
        WriteStat(sb, "Entry Points", data.EntryPoints.Count);
        WriteStat(sb, "Class Deps", dg.ClassDependencies.Count);
        WriteStat(sb, "Namespace Deps", dg.NamespaceDependencies.Count);
        WriteStat(sb, "Project Deps", dg.ProjectDependencies.Count);
        sb.AppendLine("</div></section>");
    }

    private static void WriteStat(StringBuilder sb, string label, int value)
    {
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-value\">{value}</span><span class=\"stat-label\">{Esc(label)}</span></div>");
    }

    private static void WriteEntryPoints(StringBuilder sb, List<EntryPoint> entryPoints)
    {
        if (entryPoints.Count == 0) return;

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
    }

    private static void WriteHotNodes(StringBuilder sb, List<HotNode> hotNodes)
    {
        if (hotNodes.Count == 0) return;

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
    }

    private static void WriteNamespaceDeps(StringBuilder sb, DependencyGraph graph)
    {
        if (graph.NamespaceDependencies.Count == 0) return;

        sb.AppendLine("<section id=\"architecture\">");
        sb.AppendLine("<h2>Architecture Map (Namespace Dependencies)</h2>");

        // Group by source namespace
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
    }

    private static void WriteClassIndex(StringBuilder sb, List<ClassRelationship> classes)
    {
        if (classes.Count == 0) return;

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
                sb.AppendLine($"<details class=\"class-detail\" id=\"{Esc(cls.ClassId)}\">");
                sb.AppendLine($"<summary>{Esc(cls.ClassName)} <span class=\"badge\">{Esc(cls.Kind)}</span>");
                sb.AppendLine($"<span class=\"fan\">↓{cls.FanIn} ↑{cls.FanOut}</span></summary>");
                sb.AppendLine("<div class=\"class-body\">");

                // Base class & interfaces
                if (cls.BaseClass != null)
                    sb.AppendLine($"<p><strong>Extends:</strong> {Esc(cls.BaseClass)}</p>");
                if (cls.ImplementedInterfaces.Count > 0)
                    sb.AppendLine($"<p><strong>Implements:</strong> {Esc(string.Join(", ", cls.ImplementedInterfaces))}</p>");

                // Constructor params
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

                // Depends on
                if (cls.DependsOn.Count > 0)
                {
                    sb.AppendLine("<p><strong>Depends on:</strong></p><ul>");
                    foreach (var dep in cls.DependsOn)
                        sb.AppendLine($"<li><a href=\"#{Esc(dep.ClassId)}\">{Esc(dep.ClassId)}</a> ({Esc(dep.Kind)})</li>");
                    sb.AppendLine("</ul>");
                }

                // Depended on by
                if (cls.DependedOnBy.Count > 0)
                {
                    sb.AppendLine("<p><strong>Used by:</strong></p><ul>");
                    foreach (var dep in cls.DependedOnBy)
                        sb.AppendLine($"<li><a href=\"#{Esc(dep.ClassId)}\">{Esc(dep.ClassId)}</a> ({Esc(dep.Kind)})</li>");
                    sb.AppendLine("</ul>");
                }

                sb.AppendLine("</div></details>");
            }

            sb.AppendLine("</details>");
        }
        sb.AppendLine("</section>");
    }

    private static void WriteClassExplanation(StringBuilder sb, ClassExplanation exp)
    {
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

        // State
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

        // Public API
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

        // Key flows
        if (exp.KeyFlows.Count > 0)
        {
            sb.AppendLine("<h3>Key Flows</h3><ul>");
            foreach (var flow in exp.KeyFlows)
                sb.AppendLine($"<li><code>{Esc(flow.Description)}</code></li>");
            sb.AppendLine("</ul>");
        }

        sb.AppendLine("</section>");
    }

    private static void WriteScript(StringBuilder sb)
    {
        sb.AppendLine("<script>");
        sb.AppendLine(Script);
        sb.AppendLine("</script>");
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static string Esc(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string FormatKind(string kind) => kind switch
    {
        "main" => "Main Entry Points",
        "controller-action" => "ASP.NET Controller Actions",
        "minimal-api" => "Minimal API Endpoints",
        "background-service" => "Background Services",
        "mediatr-handler" => "MediatR Handlers",
        _ => kind
    };

    // ═══════════════════════════════════════════════════════════════════════════
    // Embedded CSS & JS
    // ═══════════════════════════════════════════════════════════════════════════

    private const string Css = @"
:root { --bg: #0d1117; --fg: #c9d1d9; --border: #30363d; --accent: #58a6ff;
        --green: #3fb950; --yellow: #d29922; --red: #f85149; --purple: #bc8cff; }
* { margin: 0; padding: 0; box-sizing: border-box; }
body { font-family: 'Cascadia Code', 'Fira Code', monospace; background: var(--bg);
       color: var(--fg); line-height: 1.6; padding: 2rem; max-width: 1200px; margin: 0 auto; }
header { margin-bottom: 2rem; border-bottom: 1px solid var(--border); padding-bottom: 1rem; }
h1 { color: var(--accent); font-size: 1.5rem; }
h2 { color: var(--accent); font-size: 1.2rem; margin: 1.5rem 0 0.5rem; border-bottom: 1px solid var(--border); padding-bottom: 0.3rem; }
h3 { color: var(--purple); font-size: 1rem; margin: 1rem 0 0.3rem; }
.meta { color: #8b949e; font-size: 0.85rem; }
.toolbar { display: flex; gap: 0.5rem; margin-top: 0.5rem; }
#filter { background: var(--bg); color: var(--fg); border: 1px solid var(--border);
          padding: 0.3rem 0.6rem; border-radius: 4px; flex: 1; font-family: inherit; }
button { background: var(--border); color: var(--fg); border: none; padding: 0.3rem 0.8rem;
         border-radius: 4px; cursor: pointer; font-family: inherit; }
button:hover { background: var(--accent); color: var(--bg); }
.stats { display: flex; gap: 1.5rem; flex-wrap: wrap; margin: 0.5rem 0; }
.stat { text-align: center; }
.stat-value { display: block; font-size: 1.5rem; font-weight: bold; color: var(--accent); }
.stat-label { font-size: 0.8rem; color: #8b949e; }
table { width: 100%; border-collapse: collapse; margin: 0.5rem 0; }
th, td { padding: 0.3rem 0.6rem; text-align: left; border-bottom: 1px solid var(--border); }
th { color: var(--purple); font-size: 0.85rem; }
td { font-size: 0.85rem; }
a { color: var(--accent); text-decoration: none; }
a:hover { text-decoration: underline; }
ul { padding-left: 1.5rem; margin: 0.3rem 0; }
li { margin: 0.15rem 0; font-size: 0.85rem; }
code { background: #161b22; padding: 0.1rem 0.3rem; border-radius: 3px; font-size: 0.85rem; }
details { margin: 0.3rem 0; }
summary { cursor: pointer; padding: 0.2rem 0; }
summary:hover { color: var(--accent); }
.badge { display: inline-block; padding: 0.05rem 0.4rem; border-radius: 3px;
         font-size: 0.75rem; background: var(--border); color: var(--fg); }
.role-hub { background: var(--red); color: #fff; }
.role-provider { background: var(--green); color: var(--bg); }
.role-consumer { background: var(--yellow); color: var(--bg); }
.role-leaf { background: var(--border); }
.fan { margin-left: 0.5rem; font-size: 0.75rem; color: #8b949e; }
.class-body { padding: 0.5rem 0 0.5rem 1rem; border-left: 2px solid var(--border); margin-left: 0.5rem; }
.ns-section, .ns-group { margin: 0.3rem 0; }
section { margin-bottom: 1.5rem; }
@media print { body { background: #fff; color: #000; } a { color: #0366d6; }
  .badge { border: 1px solid #ccc; } }
";

    private const string Script = @"
function toggleAll() {
  var details = document.querySelectorAll('details');
  var anyOpen = Array.from(details).some(d => d.open);
  details.forEach(d => d.open = !anyOpen);
}
document.getElementById('filter').addEventListener('input', function(e) {
  var q = e.target.value.toLowerCase();
  document.querySelectorAll('.class-detail').forEach(function(el) {
    var text = el.textContent.toLowerCase();
    el.style.display = (!q || text.includes(q)) ? '' : 'none';
  });
  document.querySelectorAll('.ns-section').forEach(function(ns) {
    var visible = ns.querySelectorAll('.class-detail:not([style*=""display: none""])');
    ns.style.display = (!q || visible.length > 0) ? '' : 'none';
  });
});
";
}
