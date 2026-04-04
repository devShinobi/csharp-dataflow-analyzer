using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Entry point for the unified IOperation-based analysis pipeline.
/// Drop-in replacement for the old DataFlowWalker + FlowEnricher + MutationWalker trio.
/// </summary>
internal static class UnifiedAnalyzer
{
    internal static (FlowGraph flow, MutationGraph mutation) Analyze(
        SemanticModel model, CSharpCompilation compilation, string sourceFile)
    {
        var walker = new OperationWalker(model, compilation, sourceFile);
        return walker.Walk();
    }
}
