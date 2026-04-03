using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpDataFlowAnalyzer;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help") { PrintUsage(); return 0; }

        string? outputPath = null;
        bool prettyPrint = true;
        var inputPaths = new List<string>();

        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "-o" or "--output": if (i + 1 < args.Length) outputPath = args[++i]; break;
                case "--compact": prettyPrint = false; break;
                default: inputPaths.Add(args[i]); break;
            }

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

        if (files.Count == 0) { Console.Error.WriteLine("Error: no .cs files found."); return 1; }

        Console.Error.WriteLine($"Analyzing {files.Count} file(s)...");

        var syntaxTrees = files
            .Select(f => CSharpSyntaxTree.ParseText(File.ReadAllText(f), path: f))
            .ToList();

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(Console).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(List<>).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Threading.Tasks.Task).Assembly.Location),
        };
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        foreach (var dll in new[] { "System.Runtime.dll","System.Collections.dll","System.Linq.dll","netstandard.dll" })
        {
            var p = Path.Combine(runtimeDir, dll);
            if (File.Exists(p)) references.Add(MetadataReference.CreateFromFile(p));
        }

        var compilation = CSharpCompilation.Create("DataFlowAnalysis", syntaxTrees, references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Annotations));

        var results = new List<AnalysisResult>();

        foreach (var tree in syntaxTrees)
        {
            var model = compilation.GetSemanticModel(tree);

            // Pass 1: data flow graph
            var flowGraph = new DataFlowWalker(model, tree.FilePath).Walk();
            FlowEnricher.Enrich(flowGraph, compilation);

            // Pass 2: mutation graph
            var mutationGraph = new MutationWalker(model, flowGraph).Walk();

            int totalEdges = flowGraph.FlowEdges.Count
                + flowGraph.Units.Sum(u => u.Methods.Concat(u.Constructors).Sum(m => m.FlowEdges.Count));

            Console.Error.WriteLine(
                $"  {Path.GetFileName(tree.FilePath)}: " +
                $"{flowGraph.Units.Count} type(s), " +
                $"{flowGraph.Units.Sum(u => u.Methods.Count + u.Constructors.Count)} method(s), " +
                $"{totalEdges} flow edges, " +
                $"{mutationGraph.Mutations.Count} mutation(s), " +
                $"{mutationGraph.Symbols.Count(s => s.AliasIds.Count > 0)} aliased symbol(s)");

            results.Add(new AnalysisResult
            {
                Source = tree.FilePath,
                FlowGraph = flowGraph,
                MutationGraph = mutationGraph
            });
        }

        object output = results.Count == 1 ? results[0] : (object)results;

        var jsonOpts = new JsonSerializerOptions
        {
            WriteIndented = prettyPrint,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        string json = JsonSerializer.Serialize(output, jsonOpts);

        if (outputPath != null) { File.WriteAllText(outputPath, json); Console.Error.WriteLine($"Output: {outputPath}"); }
        else Console.WriteLine(json);

        return 0;
    }

    static void PrintUsage() => Console.Error.WriteLine(@"
CSharpDataFlowAnalyzer — data flow + mutation analysis for C#

Usage:
  DataFlowAnalyzer <file.cs> [<file2.cs>...] [options]
  DataFlowAnalyzer <directory/>  [options]

Options:
  -o, --output <path>    Write JSON to file (default: stdout)
  --compact              Minified JSON
  -h, --help             Help

Output sections:
  flowGraph        — classes, methods, locals, calls, flow edges (method + inter-method + inter-class)
  mutationGraph    — symbols, mutations (with guards + loop context), aliases, stateChangeSummaries");
}

public class AnalysisResult
{
    [JsonPropertyName("source")]
    public string Source { get; set; } = "";

    [JsonPropertyName("flowGraph")]
    public FlowGraph FlowGraph { get; set; } = new();

    [JsonPropertyName("mutationGraph")]
    public MutationGraph MutationGraph { get; set; } = new();
}
