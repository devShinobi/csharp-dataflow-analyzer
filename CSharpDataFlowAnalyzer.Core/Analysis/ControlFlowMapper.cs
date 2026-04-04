using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Maps <see cref="ControlFlowGraph"/> regions and branches to <see cref="ConditionGuard"/>
/// instances and loop detection.  Replaces the manual if/foreach/try nesting from the old walkers.
/// </summary>
internal sealed class ControlFlowMapper
{
    private readonly ControlFlowGraph _cfg;

    // IOperation → BasicBlock index, built lazily
    private readonly Dictionary<IOperation, BasicBlock> _opToBlock = new();

    internal ControlFlowMapper(ControlFlowGraph cfg)
    {
        _cfg = cfg;
        BuildIndex();
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the <see cref="ConditionGuard"/> for an operation based on its
    /// enclosing CFG region (try/catch/finally) and any conditional branch that
    /// gates the block it belongs to.  Returns null for unconditional code.
    /// </summary>
    internal ConditionGuard? GetGuard(IOperation operation)
    {
        if (!_opToBlock.TryGetValue(operation, out var block))
            return null;

        // Check for try/catch/finally region first
        var regionGuard = GetRegionGuard(block.EnclosingRegion);
        if (regionGuard != null) return regionGuard;

        // Check if the block is gated by a conditional branch
        return GetBranchGuard(block);
    }

    /// <summary>
    /// Returns true if the operation is inside a loop (any enclosing region
    /// that contains a back-edge).
    /// </summary>
    internal bool IsInsideLoop(IOperation operation)
    {
        if (!_opToBlock.TryGetValue(operation, out var block))
            return false;

        // Walk the region tree looking for loop patterns
        var region = block.EnclosingRegion;
        while (region != null)
        {
            // Check if any block in this region has a back-edge
            // (branches to a block with a lower or equal ordinal)
            if (HasBackEdge(region))
                return true;
            region = region.EnclosingRegion;
        }

        return false;
    }

    /// <summary>
    /// Returns the <see cref="BasicBlock"/> containing a given operation, or null.
    /// </summary>
    internal BasicBlock? GetBlock(IOperation operation)
        => _opToBlock.TryGetValue(operation, out var b) ? b : null;

    // ── Region → Guard ───────────────────────────────────────────────────────

    private static ConditionGuard? GetRegionGuard(ControlFlowRegion? region)
    {
        while (region != null)
        {
            switch (region.Kind)
            {
                case ControlFlowRegionKind.Catch:
                    return new ConditionGuard
                    {
                        Expression = region.ExceptionType?.ToDisplayString() ?? "Exception",
                        Branch = "catch",
                        ConditionKind = "try-catch"
                    };

                case ControlFlowRegionKind.Finally:
                    return new ConditionGuard
                    {
                        Expression = "always",
                        Branch = "finally",
                        ConditionKind = "try-finally"
                    };

                case ControlFlowRegionKind.Filter:
                    return new ConditionGuard
                    {
                        Expression = "filter",
                        Branch = "catch",
                        ConditionKind = "try-catch"
                    };
            }

            region = region.EnclosingRegion;
        }

        return null;
    }

    // ── Branch → Guard ───────────────────────────────────────────────────────

    private ConditionGuard? GetBranchGuard(BasicBlock block)
    {
        // Look at predecessors to find which conditional branch leads here
        foreach (var pred in block.Predecessors)
        {
            if (pred.Source.ConditionKind == ControlFlowConditionKind.None)
                continue;

            var branchValue = pred.Source.BranchValue;
            if (branchValue == null) continue;

            string condExpr = branchValue.Syntax?.ToString() ?? "";
            bool isNullCheck = condExpr.Contains("null");
            bool isNegated = pred.Source.ConditionalSuccessor?.Destination != block;

            // Determine the condition kind from the syntax
            string condKind = DetermineConditionKind(branchValue, condExpr);

            string branch = isNegated ? "else" : "then";
            string expr = isNegated ? $"!({condExpr})" : condExpr;

            return new ConditionGuard
            {
                Expression = expr,
                Branch = branch,
                ConditionKind = condKind,
                IsNullCheck = isNullCheck,
                IsNegated = isNegated,
                InvolvedSymbolIds = new List<string>() // populated later by OperationWalker
            };
        }

        return null;
    }

    private static string DetermineConditionKind(IOperation branchValue, string condExpr)
    {
        if (condExpr.Contains("null"))
            return "null-check";

        // Check the syntax parent for loop / switch / foreach context
        var syntax = branchValue.Syntax;
        while (syntax != null)
        {
            var rawKind = (SyntaxKind)syntax.RawKind;
            if (rawKind == SyntaxKind.ForEachStatement) return "foreach";
            if (rawKind == SyntaxKind.ForStatement) return "for";
            if (rawKind == SyntaxKind.WhileStatement) return "while";
            if (rawKind == SyntaxKind.DoStatement) return "while";
            if (rawKind == SyntaxKind.SwitchStatement) return "switch-case";
            if (rawKind == SyntaxKind.IfStatement) return "if";
            syntax = syntax.Parent;
        }

        return "if";
    }

    // ── Loop detection ───────────────────────────────────────────────────────

    private bool HasBackEdge(ControlFlowRegion region)
    {
        for (int i = region.FirstBlockOrdinal; i <= region.LastBlockOrdinal; i++)
        {
            var block = _cfg.Blocks[i];

            if (block.FallThroughSuccessor?.Destination is { } ft && ft.Ordinal <= block.Ordinal)
                return true;
            if (block.ConditionalSuccessor?.Destination is { } cs && cs.Ordinal <= block.Ordinal)
                return true;
        }
        return false;
    }

    // ── Index ────────────────────────────────────────────────────────────────

    private void BuildIndex()
    {
        foreach (var block in _cfg.Blocks)
            foreach (var op in block.Operations)
                IndexOperation(op, block);
    }

    private void IndexOperation(IOperation op, BasicBlock block)
    {
        _opToBlock[op] = block;
        foreach (var child in op.ChildOperations)
            IndexOperation(child, block);
    }
}
