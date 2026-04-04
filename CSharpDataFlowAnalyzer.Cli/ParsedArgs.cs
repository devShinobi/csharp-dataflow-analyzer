using System;
using System.Collections.Generic;

namespace CSharpDataFlowAnalyzer;

/// <summary>Typed representation of the CLI arguments passed to the analyzer.</summary>
public sealed record ParsedArgs(
    IReadOnlyList<string> InputPaths,
    string?       OutputPath,
    string?       TraceForwardId,
    string?       TraceBackwardId,
    int           TraceDepth,
    bool          PrettyPrint)
{
    public static ParsedArgs Parse(string[] args)
    {
        string? outputPath      = null;
        string? traceForwardId  = null;
        string? traceBackwardId = null;
        int     traceDepth      = 20;
        bool    prettyPrint     = true;
        var     inputPaths      = new List<string>();

        for (int i = 0; i < args.Length; i++)
            switch (args[i])
            {
                case "-o" or "--output":
                    if (i + 1 < args.Length) outputPath = args[++i];
                    break;
                case "--trace-forward":
                    if (i + 1 < args.Length) traceForwardId = args[++i];
                    break;
                case "--trace-backward":
                    if (i + 1 < args.Length) traceBackwardId = args[++i];
                    break;
                case "--trace-depth":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var d))
                        traceDepth = d < 1 ? 1 : d;
                    break;
                case "--compact":
                    prettyPrint = false;
                    break;
                default:
                    inputPaths.Add(args[i]);
                    break;
            }

        if (traceForwardId != null && traceBackwardId != null)
            Console.Error.WriteLine(
                "Warning: --trace-forward and --trace-backward both supplied; using forward.");

        return new ParsedArgs(inputPaths, outputPath, traceForwardId, traceBackwardId, traceDepth, prettyPrint);
    }
}
