using System;
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
        var files  = AnalyzerEngine.CollectFiles(parsed.InputPaths);

        if (files.Count == 0) { Console.Error.WriteLine("Error: no .cs files found."); return 1; }

        Console.Error.WriteLine($"Analyzing {files.Count} file(s)...");

        var results = AnalyzerEngine.AnalyzeFiles(files);
        var output  = AnalyzerEngine.BuildOutput(
                          results,
                          parsed.TraceForwardId,
                          parsed.TraceBackwardId,
                          parsed.TraceDepth);

        var json = JsonSerializer.Serialize(output, JsonOptions(parsed.PrettyPrint));

        if (parsed.OutputPath != null)
        {
            try
            {
                File.WriteAllText(parsed.OutputPath, json);
                Console.Error.WriteLine($"Output: {parsed.OutputPath}");
            }
            catch (IOException ex)
            {
                Console.Error.WriteLine($"Error: could not write '{parsed.OutputPath}': {ex.Message}");
                return 1;
            }
        }
        else
        {
            Console.WriteLine(json);
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
  DataFlowAnalyzer <file.cs> [<file2.cs>...] [options]
  DataFlowAnalyzer <directory/>  [options]

Options:
  -o, --output <path>        Write JSON to file (default: stdout)
  --compact                  Minified JSON
  --trace-forward  <id>      Traverse forward from a symbol ID (what does it affect?)
  --trace-backward <id>      Traverse backward from a symbol ID (what feeds into it?)
  --trace-depth    <n>       Max traversal depth, default 20
  -h, --help                 Help

Output sections:
  flowGraph        — classes, methods, locals, calls, flow edges (method + inter-method + inter-class)
  mutationGraph    — symbols, mutations (with guards + loop context), aliases, stateChangeSummaries
  traversal        — reachability tree (only present when --trace-forward/backward is used)");
}
