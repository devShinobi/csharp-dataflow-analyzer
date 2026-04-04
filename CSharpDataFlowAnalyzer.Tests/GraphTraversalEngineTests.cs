using System.Collections.Generic;
using System.Linq;
using Xunit;
using CSharpDataFlowAnalyzer;

namespace CSharpDataFlowAnalyzer.Tests;

// Helpers for building minimal FlowGraph / MutationGraph fixtures without Roslyn.
file static class Fixture
{
    public static FlowGraph Graph(params FlowEdge[] edges)
    {
        var g = new FlowGraph();
        g.FlowEdges.AddRange(edges);
        return g;
    }

    public static FlowEdge Edge(string from, string to, string kind = "assignment") =>
        new() { From = from, To = to, Kind = kind };

    public static MutationGraph Mutations(params MutationNode[] nodes)
    {
        var g = new MutationGraph();
        g.Mutations.AddRange(nodes);
        return g;
    }

    public static MutationNode Mutation(string id, string targetSymbolId,
        string kind = "field-write", string? methodId = null) => new()
    {
        Id             = id,
        TargetSymbolId = targetSymbolId,
        Kind           = kind,
        MethodId       = methodId ?? "SomeClass::Method[0]",
        SourceExpression = "value"
    };

    public static GraphTraversalEngine Engine(FlowGraph flow, MutationGraph? mutations = null) =>
        new(flow, mutations ?? new MutationGraph());
}

public class ForwardTraversalTests
{
    [Fact]
    public void Forward_SingleEdge_ReturnsRootWithOneChild()
    {
        var engine = Fixture.Engine(Fixture.Graph(Fixture.Edge("A", "B")));

        var result = engine.Traverse("A", TraversalDirection.Forward);

        Assert.Equal("forward", result.Direction);
        Assert.Equal("A", result.Root.SymbolId);
        Assert.Single(result.Root.Children!);
        Assert.Equal("B", result.Root.Children![0].SymbolId);
    }

    [Fact]
    public void Forward_Chain_TraversesTransitively()
    {
        // A → B → C
        var engine = Fixture.Engine(Fixture.Graph(
            Fixture.Edge("A", "B"),
            Fixture.Edge("B", "C")));

        var result = engine.Traverse("A", TraversalDirection.Forward);

        var b = result.Root.Children!.Single();
        Assert.Equal("B", b.SymbolId);
        var c = b.Children!.Single();
        Assert.Equal("C", c.SymbolId);
        Assert.Null(c.Children);
    }

    [Fact]
    public void Forward_LeafNode_HasNoChildren()
    {
        var engine = Fixture.Engine(Fixture.Graph(Fixture.Edge("A", "B")));

        var result = engine.Traverse("B", TraversalDirection.Forward);

        Assert.Null(result.Root.Children);
    }
}

public class BackwardTraversalTests
{
    [Fact]
    public void Backward_SingleEdge_ReturnsSource()
    {
        var engine = Fixture.Engine(Fixture.Graph(Fixture.Edge("A", "B")));

        var result = engine.Traverse("B", TraversalDirection.Backward);

        Assert.Equal("backward", result.Direction);
        Assert.Equal("B", result.Root.SymbolId);
        Assert.Single(result.Root.Children!);
        Assert.Equal("A", result.Root.Children![0].SymbolId);
    }

    [Fact]
    public void Backward_Chain_TracksToOrigin()
    {
        // A → B → C: backward from C should see B then A
        var engine = Fixture.Engine(Fixture.Graph(
            Fixture.Edge("A", "B"),
            Fixture.Edge("B", "C")));

        var result = engine.Traverse("C", TraversalDirection.Backward);

        Assert.Equal("B", result.Root.Children!.Single().SymbolId);
        Assert.Equal("A", result.Root.Children![0].Children!.Single().SymbolId);
    }
}

public class CycleDetectionTests
{
    [Fact]
    public void Cycle_DoesNotInfiniteLoop()
    {
        // A → B → A
        var engine = Fixture.Engine(Fixture.Graph(
            Fixture.Edge("A", "B"),
            Fixture.Edge("B", "A")));

        var result = engine.Traverse("A", TraversalDirection.Forward);

        // B's child should be A as a cycle ref, not an infinite expansion
        var b = result.Root.Children!.Single();
        var cycleA = b.Children!.Single();
        Assert.True(cycleA.IsCycleRef);
        Assert.Equal("A", cycleA.SymbolId);
        Assert.Null(cycleA.Children);
    }

    [Fact]
    public void Cycle_IsReportedInCyclesDetected()
    {
        // A → B → C → A
        var engine = Fixture.Engine(Fixture.Graph(
            Fixture.Edge("A", "B"),
            Fixture.Edge("B", "C"),
            Fixture.Edge("C", "A")));

        var result = engine.Traverse("A", TraversalDirection.Forward);

        Assert.NotNull(result.CyclesDetected);
        Assert.Contains("A", result.CyclesDetected!);
    }

    [Fact]
    public void Cycle_EachSymbolReportedOnce()
    {
        // A → B → A and A → C → A (A appears as cycle ref twice)
        var engine = Fixture.Engine(Fixture.Graph(
            Fixture.Edge("A", "B"),
            Fixture.Edge("A", "C"),
            Fixture.Edge("B", "A"),
            Fixture.Edge("C", "A")));

        var result = engine.Traverse("A", TraversalDirection.Forward);

        Assert.Equal(1, result.CyclesDetected!.Count(id => id == "A"));
    }
}

public class DepthLimitTests
{
    [Fact]
    public void DepthLimit_StopsExpansionAtMaxDepth()
    {
        // Chain of 10 nodes: 0→1→2→...→9
        var edges = Enumerable.Range(0, 9)
            .Select(i => Fixture.Edge(i.ToString(), (i + 1).ToString()))
            .ToArray();
        var engine = Fixture.Engine(Fixture.Graph(edges));

        var result = engine.Traverse("0", TraversalDirection.Forward, maxDepth: 3);

        Assert.True(result.MaxDepthReached);
        // Walk to depth 3 — node at depth 3 should have no children
        var depth3Node = result.Root               // depth 0
            .Children!.Single()                    // depth 1
            .Children!.Single()                    // depth 2
            .Children!.Single();                   // depth 3
        Assert.Null(depth3Node.Children);
    }

    [Fact]
    public void DepthLimit_WithinBounds_MaxDepthReachedIsFalse()
    {
        var engine = Fixture.Engine(Fixture.Graph(
            Fixture.Edge("A", "B"),
            Fixture.Edge("B", "C")));

        var result = engine.Traverse("A", TraversalDirection.Forward, maxDepth: 10);

        Assert.False(result.MaxDepthReached);
    }
}

public class MutationAttachmentTests
{
    [Fact]
    public void Mutation_AttachedToCorrectNode()
    {
        var flow = Fixture.Graph(Fixture.Edge("A", "B"));
        var mutations = Fixture.Mutations(
            Fixture.Mutation("m1", "B", "field-write"));
        var engine = Fixture.Engine(flow, mutations);

        var result = engine.Traverse("A", TraversalDirection.Forward);

        var b = result.Root.Children!.Single();
        Assert.NotNull(b.Mutations);
        Assert.Single(b.Mutations!);
        Assert.Equal("m1", b.Mutations![0].MutationId);
        Assert.Equal("field-write", b.Mutations![0].Kind);
    }

    [Fact]
    public void Mutation_NotCountedTwiceOnCycleRef()
    {
        // A → B → A: mutation on A should count once only
        var flow = Fixture.Graph(
            Fixture.Edge("A", "B"),
            Fixture.Edge("B", "A"));
        var mutations = Fixture.Mutations(
            Fixture.Mutation("m1", "A"));
        var engine = Fixture.Engine(flow, mutations);

        var result = engine.Traverse("A", TraversalDirection.Forward);

        Assert.Equal(1, result.MutationsFound);
    }

    [Fact]
    public void Mutation_AliasBased_AttachedViaAffectedAliasIds()
    {
        // Mutation targets "X" but also lists "A" in affectedAliasIds
        var mutation = new MutationNode
        {
            Id               = "m1",
            TargetSymbolId   = "X",
            Kind             = "property-set",
            MethodId         = "SomeClass::Method[0]",
            SourceExpression = "value",
            AffectedAliasIds = { "A" }
        };
        var mutations = new MutationGraph();
        mutations.Mutations.Add(mutation);

        var engine = new GraphTraversalEngine(Fixture.Graph(), mutations);

        var result = engine.Traverse("A", TraversalDirection.Forward);

        Assert.NotNull(result.Root.Mutations);
        Assert.Equal("m1", result.Root.Mutations![0].MutationId);
    }

    [Fact]
    public void Mutation_NoDuplicateWhenBothDirectAndAlias()
    {
        // Same mutation is both a direct hit on "A" and listed in AffectedAliasIds for "A"
        var mutation = new MutationNode
        {
            Id               = "m1",
            TargetSymbolId   = "A",
            Kind             = "field-write",
            MethodId         = "SomeClass::Method[0]",
            SourceExpression = "value",
            AffectedAliasIds = { "A" }
        };
        var mutations = new MutationGraph();
        mutations.Mutations.Add(mutation);

        var engine = new GraphTraversalEngine(Fixture.Graph(), mutations);

        var result = engine.Traverse("A", TraversalDirection.Forward);

        Assert.Single(result.Root.Mutations!);
    }
}

public class UnknownSymbolTests
{
    [Fact]
    public void UnknownSymbol_SymbolExistsReturnsFalse()
    {
        var engine = Fixture.Engine(Fixture.Graph(Fixture.Edge("A", "B")));

        Assert.False(engine.SymbolExists("Z"));
    }

    [Fact]
    public void UnknownSymbol_TraverseReturnsEmptyRootGracefully()
    {
        var engine = Fixture.Engine(Fixture.Graph(Fixture.Edge("A", "B")));

        var result = engine.Traverse("Z", TraversalDirection.Forward);

        Assert.Equal("Z", result.Root.SymbolId);
        Assert.Null(result.Root.Children);
        Assert.Equal(1, result.NodesVisited); // root itself counts as visited
    }
}

public class SymbolKindResolutionTests
{
    [Theory]
    [InlineData("MyNs.MyClass::field:_repo",           "field")]
    [InlineData("MyNs.MyClass::prop:Name",             "property")]
    [InlineData("MyNs.MyClass::ctor[0]",               "constructor")]
    [InlineData("MyNs.MyClass::DoWork[0]",             "method")]
    [InlineData("MyNs.MyClass::DoWork[0]/param:input", "param")]
    [InlineData("MyNs.MyClass::DoWork[0]/local:x",     "local")]
    [InlineData("MyNs.MyClass::DoWork[0]/call:Foo[0]", "call")]
    [InlineData("MyNs.MyClass::DoWork[0]/assign:x[0]", "assignment")]
    [InlineData("MyNs.MyClass::DoWork[0]/return[0]",   "return")]
    [InlineData("MyNs.MyClass",                        "class")]
    public void SymbolKind_ResolvedFromId(string symbolId, string expectedKind)
    {
        var engine = Fixture.Engine(Fixture.Graph(Fixture.Edge(symbolId, "other")));

        var result = engine.Traverse(symbolId, TraversalDirection.Forward);

        Assert.Equal(expectedKind, result.Root.SymbolKind);
    }
}

public class MultiFileTests
{
    [Fact]
    public void FromMultiple_MergesEdgesAcrossFiles()
    {
        // File 1 has A→B; File 2 has B→C — traversal from A should reach C
        var result1 = new AnalysisResult
        {
            FlowGraph    = Fixture.Graph(Fixture.Edge("A", "B")),
            MutationGraph = new MutationGraph()
        };
        var result2 = new AnalysisResult
        {
            FlowGraph    = Fixture.Graph(Fixture.Edge("B", "C")),
            MutationGraph = new MutationGraph()
        };

        var engine = GraphTraversalEngine.FromMultiple([result1, result2]);
        var result = engine.Traverse("A", TraversalDirection.Forward);

        var c = result.Root.Children!.Single().Children!.Single();
        Assert.Equal("C", c.SymbolId);
    }
}

public class ParsedArgsTests
{
    [Fact]
    public void Parse_InputPaths_CollectedFromPositionalArgs()
    {
        var args = ParsedArgs.Parse(["file1.cs", "file2.cs"]);

        Assert.Equal(2, args.InputPaths.Count);
        Assert.Equal("file1.cs", args.InputPaths[0]);
        Assert.Equal("file2.cs", args.InputPaths[1]);
    }

    [Fact]
    public void Parse_OutputFlag_SetsOutputPath()
    {
        var args = ParsedArgs.Parse(["-o", "out.json", "file.cs"]);
        Assert.Equal("out.json", args.OutputPath);
    }

    [Fact]
    public void Parse_OutputFlagLong_SetsOutputPath()
    {
        var args = ParsedArgs.Parse(["--output", "out.json", "file.cs"]);
        Assert.Equal("out.json", args.OutputPath);
    }

    [Fact]
    public void Parse_TraceForward_SetsId()
    {
        var args = ParsedArgs.Parse(["--trace-forward", "SomeClass::Method[0]", "file.cs"]);
        Assert.Equal("SomeClass::Method[0]", args.TraceForwardId);
        Assert.Null(args.TraceBackwardId);
    }

    [Fact]
    public void Parse_TraceBackward_SetsId()
    {
        var args = ParsedArgs.Parse(["--trace-backward", "SomeClass::Method[0]", "file.cs"]);
        Assert.Equal("SomeClass::Method[0]", args.TraceBackwardId);
        Assert.Null(args.TraceForwardId);
    }

    [Fact]
    public void Parse_TraceDepth_Parsed()
    {
        var args = ParsedArgs.Parse(["--trace-depth", "5", "file.cs"]);
        Assert.Equal(5, args.TraceDepth);
    }

    [Fact]
    public void Parse_TraceDepthBelowOne_ClampedToOne()
    {
        var args = ParsedArgs.Parse(["--trace-depth", "0", "file.cs"]);
        Assert.Equal(1, args.TraceDepth);
    }

    [Fact]
    public void Parse_CompactFlag_DisablesPrettyPrint()
    {
        var args = ParsedArgs.Parse(["--compact", "file.cs"]);
        Assert.False(args.PrettyPrint);
    }

    [Fact]
    public void Parse_Defaults_AreCorrect()
    {
        var args = ParsedArgs.Parse(["file.cs"]);

        Assert.Null(args.OutputPath);
        Assert.Null(args.TraceForwardId);
        Assert.Null(args.TraceBackwardId);
        Assert.Equal(20, args.TraceDepth);
        Assert.True(args.PrettyPrint);
    }
}

public class AnalyzerEngineBuildOutputTests
{
    private static AnalysisResult MakeResult(string source = "test.cs") => new()
    {
        Source        = source,
        FlowGraph     = new FlowGraph(),
        MutationGraph = new MutationGraph()
    };

    [Fact]
    public void BuildOutput_NoTrace_SingleFile_ReturnsAnalysisResult()
    {
        var results = new List<AnalysisResult> { MakeResult() };

        var output = AnalyzerEngine.BuildOutput(results, null, null, 20);

        Assert.IsType<AnalysisResult>(output);
    }

    [Fact]
    public void BuildOutput_NoTrace_MultiFile_ReturnsList()
    {
        var results = new List<AnalysisResult> { MakeResult("a.cs"), MakeResult("b.cs") };

        var output = AnalyzerEngine.BuildOutput(results, null, null, 20);

        Assert.IsType<List<AnalysisResult>>(output);
    }

    [Fact]
    public void BuildOutput_TraceForward_SingleFile_ReturnsAnalysisResultWithTraversal()
    {
        var result = MakeResult();
        result.FlowGraph.FlowEdges.Add(new FlowEdge { From = "A", To = "B", Kind = "assignment" });
        var results = new List<AnalysisResult> { result };

        var output = AnalyzerEngine.BuildOutput(results, "A", null, 20);

        var ar = Assert.IsType<AnalysisResult>(output);
        Assert.NotNull(ar.Traversal);
        Assert.Equal("forward", ar.Traversal!.Direction);
    }

    [Fact]
    public void BuildOutput_TraceForward_DoesNotMutateOriginalResult()
    {
        var result = MakeResult();
        var results = new List<AnalysisResult> { result };

        AnalyzerEngine.BuildOutput(results, "A", null, 20);

        // The original entry must not have been modified
        Assert.Null(result.Traversal);
    }

    [Fact]
    public void BuildOutput_TraceForward_MultiFile_ReturnsMultiFileOutput()
    {
        var results = new List<AnalysisResult> { MakeResult("a.cs"), MakeResult("b.cs") };

        var output = AnalyzerEngine.BuildOutput(results, "A", null, 20);

        Assert.IsType<MultiFileOutput>(output);
    }

    [Fact]
    public void BuildOutput_TraceBackward_UsesBackwardDirection()
    {
        var result = MakeResult();
        result.FlowGraph.FlowEdges.Add(new FlowEdge { From = "A", To = "B", Kind = "assignment" });
        var results = new List<AnalysisResult> { result };

        var output = AnalyzerEngine.BuildOutput(results, null, "B", 20);

        var ar = Assert.IsType<AnalysisResult>(output);
        Assert.Equal("backward", ar.Traversal!.Direction);
    }
}
