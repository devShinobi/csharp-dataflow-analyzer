using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Core analysis pipeline: file discovery → Roslyn compilation → data flow +
/// mutation analysis → optional traversal.  Has no dependency on the CLI layer.
/// </summary>
public static class AnalyzerEngine
{
    // ── File discovery ────────────────────────────────────────────────────────

    /// <summary>
    /// Expands a mix of file paths and directory paths into a flat list of
    /// <c>.cs</c> source files.  Unknown paths are skipped with a warning.
    /// </summary>
    /// <param name="log">Receives diagnostic messages. Defaults to <c>Console.Error.WriteLine</c>.</param>
    public static List<string> CollectFiles(IEnumerable<string> inputPaths, Action<string>? log = null)
    {
        log ??= Console.Error.WriteLine;
        var files = new List<string>();
        foreach (var path in inputPaths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
            else if (File.Exists(path))
                files.Add(path);
            else
                log($"Warning: '{path}' not found — skipping.");
        }
        return files;
    }

    // ── Analysis entry point ──────────────────────────────────────────────────

    /// <summary>
    /// Full pipeline: parses <paramref name="filePaths"/>, compiles, and runs both
    /// analysis passes.  Callers outside this assembly do not need a Roslyn reference.
    /// </summary>
    /// <param name="log">Receives diagnostic messages. Defaults to <c>Console.Error.WriteLine</c>.</param>
    public static List<AnalysisResult> AnalyzeFiles(IEnumerable<string> filePaths, Action<string>? log = null)
    {
        var compilation = BuildCompilation(filePaths);
        return Analyze(compilation, log);
    }

    // ── Roslyn compilation (internal — Roslyn types stay inside Core) ─────────

    internal static CSharpCompilation BuildCompilation(IEnumerable<string> filePaths)
    {
        var trees = filePaths
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
            .ToList();

        var references = BuildReferences();

        return CSharpCompilation.Create(
            "DataFlowAnalysis",
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Annotations));
    }

    private static List<MetadataReference> BuildReferences()
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
        };

        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        foreach (var dll in new[] { "System.Runtime.dll", "System.Collections.dll", "System.Linq.dll", "netstandard.dll" })
        {
            var path = Path.Combine(runtimeDir, dll);
            if (File.Exists(path)) refs.Add(MetadataReference.CreateFromFile(path));
        }

        return refs;
    }

    // ── Analysis passes ───────────────────────────────────────────────────────

    internal static List<AnalysisResult> Analyze(CSharpCompilation compilation, Action<string>? log = null)
    {
        log ??= Console.Error.WriteLine;
        var results = new List<AnalysisResult>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);

            var (flowGraph, mutationGraph) = UnifiedAnalyzer.Analyze(model, compilation, tree.FilePath);

            LogFileSummary(tree.FilePath, flowGraph, mutationGraph, log);

            results.Add(new AnalysisResult
            {
                Source        = tree.FilePath,
                FlowGraph     = flowGraph,
                MutationGraph = mutationGraph
            });
        }

        return results;
    }

    private static void LogFileSummary(string filePath, FlowGraph fg, MutationGraph mg, Action<string> log)
    {
        int totalEdges = fg.FlowEdges.Count
            + fg.Units.Sum(u => u.Methods.Concat(u.Constructors).Sum(m => m.FlowEdges.Count));

        log($"  {Path.GetFileName(filePath)}: " +
            $"{fg.Units.Count} type(s), " +
            $"{fg.Units.Sum(u => u.Methods.Count + u.Constructors.Count)} method(s), " +
            $"{totalEdges} flow edges, " +
            $"{mg.Mutations.Count} mutation(s), " +
            $"{mg.Symbols.Count(s => s.AliasIds.Count > 0)} aliased symbol(s)");
    }

    // ── Output assembly ───────────────────────────────────────────────────────

    /// <summary>
    /// Optionally runs traversal and returns the appropriate output object.
    /// Returns <c>object</c> because the shape varies by file count and trace mode:
    /// single-file → <see cref="AnalysisResult"/>, multi-file → <c>List&lt;AnalysisResult&gt;</c>,
    /// multi-file+trace → <see cref="MultiFileOutput"/>.
    /// <c>System.Text.Json</c> serializes the runtime type correctly in all cases.
    /// </summary>
    /// <param name="log">Receives diagnostic messages. Defaults to <c>Console.Error.WriteLine</c>.</param>
    public static object BuildOutput(
        List<AnalysisResult> results,
        string?              traceForwardId,
        string?              traceBackwardId,
        int                  traceDepth,
        Action<string>?      log = null)
    {
        log ??= Console.Error.WriteLine;

        if (traceForwardId == null && traceBackwardId == null)
            return results.Count == 1 ? (object)results[0] : results;

        var traceId   = (traceForwardId ?? traceBackwardId)!;
        var direction = traceForwardId != null ? TraversalDirection.Forward : TraversalDirection.Backward;
        var engine    = GraphTraversalEngine.FromMultiple(results);

        if (!engine.SymbolExists(traceId))
            log($"Warning: symbol '{traceId}' not found in analyzed file(s).");

        var traversal = engine.Traverse(traceId, direction, traceDepth);
        log($"  Traversal ({direction}): {traversal.NodesVisited} node(s), " +
            $"{traversal.MutationsFound} mutation(s), " +
            $"{traversal.CyclesDetected?.Count ?? 0} cycle(s)");

        if (results.Count == 1)
        {
            // Return a new instance — do not mutate the original result.
            return new AnalysisResult
            {
                Source        = results[0].Source,
                FlowGraph     = results[0].FlowGraph,
                MutationGraph = results[0].MutationGraph,
                Traversal     = traversal
            };
        }

        return new MultiFileOutput { Results = results, Traversal = traversal };
    }
}
