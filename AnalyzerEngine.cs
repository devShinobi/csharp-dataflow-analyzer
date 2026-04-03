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
    public static List<string> CollectFiles(IEnumerable<string> inputPaths)
    {
        var files = new List<string>();
        foreach (var path in inputPaths)
        {
            if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
            else if (File.Exists(path))
                files.Add(path);
            else
                Console.Error.WriteLine($"Warning: '{path}' not found — skipping.");
        }
        return files;
    }

    // ── Roslyn compilation ────────────────────────────────────────────────────

    /// <summary>Parses <paramref name="filePaths"/> and creates a Roslyn compilation.</summary>
    public static CSharpCompilation BuildCompilation(IEnumerable<string> filePaths)
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

    /// <summary>
    /// Runs both analysis passes (data flow + mutation) over every syntax tree
    /// in <paramref name="compilation"/> and returns one result per file.
    /// </summary>
    public static List<AnalysisResult> Analyze(CSharpCompilation compilation)
    {
        var results = new List<AnalysisResult>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);

            var flowGraph = new DataFlowWalker(model, tree.FilePath).Walk();
            FlowEnricher.Enrich(flowGraph, compilation);

            var mutationGraph = new MutationWalker(model, flowGraph).Walk();

            LogFileSummary(tree.FilePath, flowGraph, mutationGraph);

            results.Add(new AnalysisResult
            {
                Source        = tree.FilePath,
                FlowGraph     = flowGraph,
                MutationGraph = mutationGraph
            });
        }

        return results;
    }

    private static void LogFileSummary(string filePath, FlowGraph fg, MutationGraph mg)
    {
        int totalEdges = fg.FlowEdges.Count
            + fg.Units.Sum(u => u.Methods.Concat(u.Constructors).Sum(m => m.FlowEdges.Count));

        Console.Error.WriteLine(
            $"  {Path.GetFileName(filePath)}: " +
            $"{fg.Units.Count} type(s), " +
            $"{fg.Units.Sum(u => u.Methods.Count + u.Constructors.Count)} method(s), " +
            $"{totalEdges} flow edges, " +
            $"{mg.Mutations.Count} mutation(s), " +
            $"{mg.Symbols.Count(s => s.AliasIds.Count > 0)} aliased symbol(s)");
    }

    // ── Output assembly ───────────────────────────────────────────────────────

    /// <summary>
    /// Optionally runs traversal and returns the appropriate output object:
    /// <list type="bullet">
    ///   <item>Single file, no trace → <see cref="AnalysisResult"/></item>
    ///   <item>Multi file, no trace  → <c>List&lt;AnalysisResult&gt;</c></item>
    ///   <item>Single file, trace    → <see cref="AnalysisResult"/> with <c>Traversal</c> set</item>
    ///   <item>Multi file, trace     → <see cref="MultiFileOutput"/></item>
    /// </list>
    /// </summary>
    public static object BuildOutput(
        List<AnalysisResult> results,
        string?              traceForwardId,
        string?              traceBackwardId,
        int                  traceDepth)
    {
        if (traceForwardId == null && traceBackwardId == null)
            return results.Count == 1 ? (object)results[0] : results;

        var traceId   = (traceForwardId ?? traceBackwardId)!;
        var direction = traceForwardId != null ? TraversalDirection.Forward : TraversalDirection.Backward;
        var engine    = GraphTraversalEngine.FromMultiple(results);

        if (!engine.SymbolExists(traceId))
            Console.Error.WriteLine($"Warning: symbol '{traceId}' not found in analyzed file(s).");

        var traversal = engine.Traverse(traceId, direction, traceDepth);
        Console.Error.WriteLine(
            $"  Traversal ({direction}): {traversal.NodesVisited} node(s), " +
            $"{traversal.MutationsFound} mutation(s), " +
            $"{traversal.CyclesDetected?.Count ?? 0} cycle(s)");

        if (results.Count == 1)
        {
            results[0].Traversal = traversal;
            return results[0];
        }

        return new MultiFileOutput { Results = results, Traversal = traversal };
    }
}
