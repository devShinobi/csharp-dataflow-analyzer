using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CSharpDataFlowAnalyzer.Analysis;
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
    /// Expands a mix of file paths, directory paths, and solution/project paths
    /// into a flat list of <c>.cs</c> source files.  Solution/project files are
    /// separated out into <paramref name="solutionOrProjectPath"/> when found.
    /// Unknown paths are skipped with a warning.
    /// </summary>
    /// <param name="solutionOrProjectPath">
    /// Set to the first <c>.sln</c>/<c>.slnx</c>/<c>.csproj</c> found in
    /// <paramref name="inputPaths"/>, or <c>null</c> when only loose files/dirs
    /// are provided.
    /// </param>
    /// <param name="log">Receives diagnostic messages. Defaults to <c>Console.Error.WriteLine</c>.</param>
    public static List<string> CollectFiles(
        IEnumerable<string> inputPaths,
        out string? solutionOrProjectPath,
        Action<string>? log = null)
    {
        log ??= Console.Error.WriteLine;
        solutionOrProjectPath = null;
        var files = new List<string>();

        foreach (var path in inputPaths)
        {
            if (SolutionLoader.CanLoad(path))
            {
                if (solutionOrProjectPath == null)
                    solutionOrProjectPath = path;
                else
                    log($"Warning: multiple solution/project inputs — using '{solutionOrProjectPath}', ignoring '{path}'.");
            }
            else if (Directory.Exists(path))
                files.AddRange(Directory.GetFiles(path, "*.cs", SearchOption.AllDirectories));
            else if (File.Exists(path))
                files.Add(path);
            else
                log($"Warning: '{path}' not found — skipping.");
        }

        return files;
    }

    /// <summary>Overload for backward compatibility — ignores solution/project detection.</summary>
    public static List<string> CollectFiles(IEnumerable<string> inputPaths, Action<string>? log = null)
    {
        return CollectFiles(inputPaths, out _, log);
    }

    // ── Analysis entry points ─────────────────────────────────────────────────

    /// <summary>
    /// Full pipeline for loose <c>.cs</c> files: parses, compiles with BCL
    /// references, and runs both analysis passes.
    /// </summary>
    /// <param name="log">Receives diagnostic messages. Defaults to <c>Console.Error.WriteLine</c>.</param>
    public static List<AnalysisResult> AnalyzeFiles(IEnumerable<string> filePaths, Action<string>? log = null)
    {
        var compilation = BuildCompilation(filePaths);
        return Analyze(compilation, log);
    }

    /// <summary>
    /// Full pipeline for a solution or project file: uses <see cref="SolutionLoader"/>
    /// to resolve projects, NuGet packages, and framework references, then builds
    /// per-project Roslyn compilations and analyzes each.
    /// </summary>
    /// <param name="log">Receives diagnostic messages. Defaults to <c>Console.Error.WriteLine</c>.</param>
    public static List<AnalysisResult> AnalyzeSolution(string solutionOrProjectPath, Action<string>? log = null)
    {
        log ??= Console.Error.WriteLine;

        var projects = SolutionLoader.Load(solutionOrProjectPath, log);

        if (projects.Count == 0)
        {
            log("Warning: no projects loaded — falling back to empty result.");
            return new List<AnalysisResult>();
        }

        var allResults = new List<AnalysisResult>();

        foreach (var project in projects)
        {
            if (project.SourceFiles.Count == 0)
            {
                log($"  {Path.GetFileName(project.ProjectPath)}: no source files — skipping.");
                continue;
            }

            log($"Compiling {Path.GetFileName(project.ProjectPath)} " +
                $"({project.SourceFiles.Count} file(s), {project.ReferencePaths.Count} ref(s))...");

            var compilation = BuildCompilation(project);
            var results = Analyze(compilation, log);
            allResults.AddRange(results);
        }

        return allResults;
    }

    // ── Roslyn compilation (internal — Roslyn types stay inside Core) ─────────

    /// <summary>Loose-file compilation with BCL-only references.</summary>
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

    /// <summary>
    /// Project-aware compilation using resolved references from <see cref="SolutionLoader"/>.
    /// </summary>
    internal static CSharpCompilation BuildCompilation(ProjectInfo project)
    {
        var trees = project.SourceFiles
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
            .ToList();

        var references = project.ReferencePaths
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        var outputKind = project.OutputType.Equals("Exe", StringComparison.OrdinalIgnoreCase)
            ? OutputKind.ConsoleApplication
            : OutputKind.DynamicallyLinkedLibrary;

        return CSharpCompilation.Create(
            project.AssemblyName,
            trees,
            references,
            new CSharpCompilationOptions(
                outputKind,
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
        var trees = compilation.SyntaxTrees.ToList();

        // Single file — no parallelism overhead needed.
        if (trees.Count <= 1)
        {
            var results = new List<AnalysisResult>();
            foreach (var tree in trees)
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

        // Multiple files — analyze in parallel.
        // SemanticModel reads from a shared compilation are thread-safe;
        // each OperationWalker builds independent per-file graphs.
        var bag = new ConcurrentBag<AnalysisResult>();

        Parallel.ForEach(trees, tree =>
        {
            var model = compilation.GetSemanticModel(tree);
            var (flowGraph, mutationGraph) = UnifiedAnalyzer.Analyze(model, compilation, tree.FilePath);
            bag.Add(new AnalysisResult
            {
                Source        = tree.FilePath,
                FlowGraph     = flowGraph,
                MutationGraph = mutationGraph
            });
        });

        // Sort by file path for deterministic output, then log.
        var sorted = bag.OrderBy(r => r.Source, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in sorted)
            LogFileSummary(r.Source, r.FlowGraph, r.MutationGraph, log);

        return sorted;
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

    // ── Onboarding (Phase 2) ─────────────────────────────────────────────────

    /// <summary>
    /// Post-processes analysis results to produce an onboarding report:
    /// dependency graph, entry points, hot nodes, and optional class explanation.
    /// </summary>
    /// <param name="log">Receives diagnostic messages. Defaults to <c>Console.Error.WriteLine</c>.</param>
    public static OnboardingOutput BuildOnboarding(
        List<AnalysisResult> results,
        string?              explainClassId = null,
        Action<string>?      log = null)
    {
        log ??= Console.Error.WriteLine;

        log("Building onboarding report...");
        var (graph, relationships) = DependencyAnalyzer.Analyze(results, log);
        var entryPoints = EntryPointDetector.Detect(results, log);
        var hotNodes = HotPathDetector.Detect(relationships, topN: 10, log);

        ClassExplanation? explanation = null;
        if (explainClassId != null)
            explanation = ClassExplainer.Explain(explainClassId, results, relationships, entryPoints, hotNodes, log);

        return new OnboardingOutput
        {
            DependencyGraph = graph,
            EntryPoints = entryPoints,
            ClassRelationships = relationships,
            HotNodes = hotNodes,
            ClassExplanation = explanation,
            Results = results
        };
    }
}
