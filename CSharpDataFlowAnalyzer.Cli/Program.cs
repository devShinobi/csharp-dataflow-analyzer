using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CSharpDataFlowAnalyzer;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { PrintUsage(); return 0; }

        var parsed = ParsedArgs.Parse(args);
        var files  = AnalyzerEngine.CollectFiles(parsed.InputPaths, out var solutionPath);

        List<AnalysisResult> results;
        if (solutionPath != null)
        {
            Console.Error.WriteLine($"Loading solution/project: {solutionPath}");
            results = AnalyzerEngine.AnalyzeSolution(solutionPath);
        }
        else
        {
            if (files.Count == 0) { Console.Error.WriteLine("Error: no .cs files found."); return 1; }
            Console.Error.WriteLine($"Analyzing {files.Count} file(s)...");
            results = AnalyzerEngine.AnalyzeFiles(files);
        }

        if (results.Count == 0) { Console.Error.WriteLine("Error: no analysis results produced."); return 1; }

        // Onboarding mode: dependency graph, entry points, hot nodes, class explanation
        if (parsed.Onboard)
        {
            var onboarding = AnalyzerEngine.BuildOnboarding(results, parsed.ExplainClassId);

            string content;
            if (parsed.Format == "html")
                content = HtmlReportGenerator.Generate(onboarding);
            else
                content = JsonSerializer.Serialize(onboarding, JsonOptions(parsed.PrettyPrint));

            return WriteOutput(content, parsed.OutputPath,
                parsed.Format == "html" ? "html" : "json");
        }

        // Standard analysis mode: flow graph + mutation graph + optional traversal
        var output  = AnalyzerEngine.BuildOutput(
                          results,
                          parsed.TraceForwardId,
                          parsed.TraceBackwardId,
                          parsed.TraceDepth);

        var json = JsonSerializer.Serialize(output, JsonOptions(parsed.PrettyPrint));
        return WriteOutput(json, parsed.OutputPath, "json");
    }

    static int WriteOutput(string content, string? outputPath, string format)
    {
        if (outputPath != null)
        {
            try
            {
                File.WriteAllText(outputPath, content);
                Console.Error.WriteLine($"Output ({format}): {outputPath}");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: could not write '{outputPath}': {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.WriteLine(content);
        }
        return 0;
    }

    static JsonSerializerOptions JsonOptions(bool pretty) => new()
    {
        WriteIndented          = pretty,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // UnsafeRelaxedJsonEscaping: output is JSON-to-file/stdout, not HTML-embedded.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    static void PrintUsage() => Console.Error.WriteLine(@"
CSharpDataFlowAnalyzer — data flow + mutation analysis for C#

Usage:
  DataFlowAnalyzer <file.cs|dir/|project.csproj|solution.sln> [options]

Analysis options:
  -o, --output <path>        Write output to file (default: stdout)
  --compact                  Minified JSON
  --trace-forward  <id>      Traverse forward from a symbol ID (what does it affect?)
  --trace-backward <id>      Traverse backward from a symbol ID (what feeds into it?)
  --trace-depth    <n>       Max traversal depth, default 20

Onboarding options:
  --onboard                  Produce onboarding report (dependencies, entry points, hot nodes)
  --explain <classId>        Deep-dive into a single class (implies --onboard)
  --format  <json|html>      Output format, default: json

  -h, --help                 Help

Output sections (analysis mode):
  flowGraph        — classes, methods, locals, calls, flow edges
  mutationGraph    — symbols, mutations (with guards + loop context), aliases
  traversal        — reachability tree (when --trace-forward/backward is used)

Output sections (onboarding mode):
  dependencyGraph      — project/namespace/class dependency edges
  entryPoints          — detected application entry points
  classRelationships   — per-class dependencies, interfaces, DI params
  hotNodes             — most-connected classes ranked by connectivity
  classExplanation     — detailed class explanation (when --explain is used)");
}
