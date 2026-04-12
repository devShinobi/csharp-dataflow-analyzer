# Phase 3 — Flow Understanding: "How does data get from A to B?"

## Context

Phase 2 (Onboarding: Codebase Understanding) is complete — the tool can answer "show me the architecture, entry points, and hot classes." Phase 3 builds on the same `AnalysisResult[]` foundation to answer: **"When this method is called, show me every data transformation, every method hop, every DB write that happens — end to end."**

All Phase 3 features follow a **Test-Driven Development** workflow: tests are written first (RED), then implemented (GREEN), then refactored. Each feature step includes a test-first sub-step before the implementation sub-step.

---

## Goals

> **"How does data get from A to B?"** — Answer path queries between any two points in the codebase.

---

## MVP vs Stretch

**Core MVP** (must ship for Phase 3 to be useful):
1. Point-to-point path query — `--trace-path <sourceId> <targetId>`
2. Call graph extraction — method→method, filterable by namespace/class
3. LINQ pipeline tracing — connect existing `LinqChain` data to traversal (quick win, no Roslyn changes)
4. Exception flow capture + builder

**Stretch** (Phase 3.5 — deliver after MVP is stable):
5. Async/await flow tracking
6. HTTP request flow tracing (controller → service → repo → DB)
7. Taint analysis (mark input as tainted, trace where it flows)

---

## TDD Workflow (per step)

Each implementation step follows this exact sequence:

```
1. Write test(s) → run → confirm RED (compilation failure or assertion failure)
2. Write minimal implementation → run → confirm GREEN (tests pass)
3. Refactor if needed → run → confirm still GREEN
4. dotnet build — 0 errors, 0 warnings
```

Do not proceed to the next step until the current step is GREEN and building cleanly.

---

## Implementation Steps

---

### Step 1: Phase 3 Models
**Create**: `Core/FlowModels.cs`

All new model types in one file, following existing conventions:
- Mutable `{ get; set; }` with `= new()` collection initializers
- `[JsonPropertyName("camelCase")]` on all properties
- `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]` on optional properties
- No Roslyn types

| Model | Purpose |
|-------|---------|
| `PathQueryResult` | Wraps one or more `FlowPath` objects between a source and target symbol |
| `FlowPath` | Ordered list of `FlowPathStep` objects representing one discovered path |
| `FlowPathStep` | `symbolId`, `symbolKind`, `edgeKind`, `edgeLabel`, `depth`, optional `mutations` attached at this step |
| `CallGraph` | `nodes` (list of `CallGraphNode`) + `edges` (list of `CallGraphEdge`) |
| `CallGraphNode` | `methodId`, `className`, `namespace`, `methodName`, `isAsync`, `isEntryPoint` |
| `CallGraphEdge` | `callerId`, `calleeId`, `callSite` (call node ID), `isAwaited`, `edgeKind` |
| `ExceptionFlow` | `throwSite` (methodId), `exceptionType`, `catchSite` (methodId or null), `propagationChain` (list of methodIds) |
| `ExceptionEdge` | `fromMethodId`, `toMethodId`, `exceptionType`, `isCaught`, `catchExpression` |
| `TaintResult` | `sourceId`, `taintLabel`, `reachableSymbols` (list of `TaintedNode`) |
| `TaintedNode` | `symbolId`, `symbolKind`, `pathFromSource` (`FlowPath`), `isSink`, `sinkKind` |
| `AsyncFlowEdge` | `awaitSiteId`, `continuationMethodId`, `taskSymbolId`, `isConfigureAwait` |
| `HttpRequestFlow` | `entryPoint` (`EntryPoint` from Phase 2), `sinkMethodId`, `sinkKind`, `path` (`FlowPath`) |
| `FlowUnderstandingOutput` | Top-level wrapper: optional `pathQuery`, `callGraph`, `exceptionFlows`, `taintResult`, `httpFlows`; always includes `results` |

**No tests required for this step** — pure data models with no logic to verify. Verify with `dotnet build`.

---

### Step 2: Extend Output Models
**Modify**: `Core/OutputModels.cs`

Add optional `FlowUnderstandingOutput` to `AnalysisResult` and `MultiFileOutput`:

```csharp
[JsonPropertyName("flowUnderstanding")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public FlowUnderstandingOutput? FlowUnderstanding { get; set; }
```

**No tests required** — structural change verified by `dotnet build`.

---

### Step 3: LINQ Pipeline Tracing (quick win)
**Modify**: `Core/Analysis/OperationWalker.cs`, `Core/Models.cs`

`LinqChain` data is already captured in `MethodNode.LinqChains` but the individual chain steps are not connected by `FlowEdge` entries. Fix this:

In `OperationWalker`, after assembling each `LinqChain`, emit `FlowEdge` entries connecting consecutive steps:

```csharp
// In AssembleLinqChains (or wherever chains are finalized):
for (int i = 0; i < chain.Steps.Count - 1; i++)
    method.FlowEdges.Add(new FlowEdge
    {
        From = chain.Steps[i].CallId,
        To   = chain.Steps[i + 1].CallId,
        Kind = "linq-pipeline",
        Label = $"{chain.Steps[i].MethodName} → {chain.Steps[i + 1].MethodName}"
    });
```

Add `"linq-pipeline"` to the `FlowEdge.Kind` comment block in `Models.cs`.

#### TDD

**Tests first** (`Tests/FlowUnderstandingTests.cs` — new file, `LinqTraversalTests` class):

```
RED tests:
- LINQ chain steps appear as "linq-pipeline" FlowEdge entries in the method's FlowEdges
- Forward traversal starting from the first chain step reaches all subsequent steps
- Traversal stops at the terminal step and does not loop back
```

Write these test stubs and confirm they fail (no `linq-pipeline` edges exist yet), then implement.

---

### Step 4: Path Finder Engine
**Create**: `Core/Analysis/PathFinder.cs` (`internal sealed`)

BFS-based path finder across the FlowGraph edge index. Finds **all paths** between two symbol IDs with no artificial limit on number of paths or traversal depth. The only termination condition is exhaustion of reachable nodes or cycle detection (a path cannot revisit a node it has already visited in its own chain).

Design:

1. Accept `List<AnalysisResult>`, `sourceId`, `targetId`
2. Build forward edge index (same shape as `GraphTraversalEngine` uses)
3. BFS from `sourceId`; maintain per-path visited set (not global) so distinct paths to the same node are all found
4. For each complete path (sourceId → … → targetId), collect the edge chain as `List<FlowPathStep>`
5. Attach mutations to each step by looking up the mutation index (same pattern as `GraphTraversalEngine`)
6. Cycle detection: a path terminates if the candidate next node is already in that path's own visited set
7. Return `PathQueryResult` with all found paths, sorted by length (shortest first)

```csharp
internal sealed class PathFinder
{
    public static PathQueryResult FindPaths(
        List<AnalysisResult> results,
        string sourceId,
        string targetId,
        Action<string>? log = null)
}
```

#### TDD

**Tests first** (`FlowUnderstandingTests.cs` — `PathFinderTests` class):

```
RED tests:
- Single direct path (A→B edge exists) returns one FlowPath with two steps
- Multi-hop path (A→B→C) returns one FlowPath with three steps
- Multiple distinct paths (A→B→D and A→C→D) both returned
- No path exists returns PathQueryResult with empty Paths list
- Cycle in graph (A→B→A) does not infinite-loop; path containing cycle is abandoned
- Source == target returns a single-step path (trivial path)
- Mutations at a step are attached to the corresponding FlowPathStep
```

Write these tests → confirm RED → implement PathFinder → confirm GREEN.

---

### Step 5: Wire Path Query to CLI
**Modify**: `Cli/ParsedArgs.cs`, `Cli/Program.cs`, `Core/AnalyzerEngine.cs`

`ParsedArgs` additions:
- `--trace-path <sourceId> <targetId>` — two string args

```csharp
// ParsedArgs additions:
public string? TracePathSourceId { get; init; }
public string? TracePathTargetId { get; init; }
```

`AnalyzerEngine` new public method:
```csharp
public static PathQueryResult FindPaths(
    List<AnalysisResult> results,
    string sourceId,
    string targetId,
    Action<string>? log = null)
```

`Program.cs`: when `TracePathSourceId` is set, call `FindPaths`, attach result to `FlowUnderstandingOutput`, serialize and output.

#### TDD

**Tests first** (`FlowUnderstandingTests.cs` — `ParsedArgsPhase3Tests` class, initial entries):

```
RED tests:
- "--trace-path A B" sets TracePathSourceId="A", TracePathTargetId="B"
- "--trace-path" with only one arg throws / sets error state
- "--trace-path" with no args is rejected cleanly
```

Write → RED → implement → GREEN.

---

### Step 6: Call Graph Builder
**Create**: `Core/Analysis/CallGraphBuilder.cs` (`internal sealed`)

Post-processes `List<AnalysisResult>` to extract the method-to-method call graph:

1. For each `ClassUnit` → each `MethodNode` → each `CallNode` with a non-null `ResolvedMethodId`, emit a `CallGraphEdge`
2. Build `CallGraphNode` for every methodId that appears as caller or callee
3. Annotate nodes: `isAsync` from `MethodNode.IsAsync`, `isEntryPoint` from Phase 2's `EntryPointDetector` (pass `List<EntryPoint>` if available, else false)
4. Support filtering: by namespace prefix, by class ID
5. Dedup: if the same caller-callee pair appears at multiple call sites, emit one edge per call site (distinct `callSite` ID) rather than deduping

```csharp
internal sealed class CallGraphBuilder
{
    public static CallGraph Build(
        List<AnalysisResult> results,
        string? filterNamespace = null,
        string? filterClassId = null,
        List<EntryPoint>? entryPoints = null,
        Action<string>? log = null)
}
```

#### TDD

**Tests first** (`FlowUnderstandingTests.cs` — `CallGraphBuilderTests` class):

```
RED tests:
- Method with one CallNode (resolved) produces one edge and two nodes
- Namespace filter excludes methods not in that namespace
- Class filter excludes methods not in that class
- IsAsync annotation is set correctly when MethodNode.IsAsync == true
- IsEntryPoint annotation is set when the method appears in EntryPoints list
- Multiple call sites between same caller/callee produce multiple edges (not deduped)
- Method with no calls produces one node, zero edges
```

Write → RED → implement → GREEN.

---

### Step 7: Wire Call Graph to CLI
**Modify**: `Cli/ParsedArgs.cs`, `Cli/Program.cs`, `Core/AnalyzerEngine.cs`

`ParsedArgs` additions:
- `--call-graph` (bool flag)
- `--filter-ns <prefix>` (string, optional)
- `--filter-class <classId>` (string, optional)

`AnalyzerEngine` new public method:
```csharp
public static CallGraph BuildCallGraph(
    List<AnalysisResult> results,
    string? filterNamespace = null,
    string? filterClassId = null,
    Action<string>? log = null)
```

#### TDD

**Tests first** (add to `ParsedArgsPhase3Tests`):

```
RED tests:
- "--call-graph" sets CallGraph=true
- "--call-graph --filter-ns Foo.Bar" sets CallGraph=true and FilterNs="Foo.Bar"
- "--filter-ns" without "--call-graph" still parses (flag is independent)
- "--filter-class Foo::Bar" sets FilterClass="Foo::Bar"
```

Write → RED → implement → GREEN.

---

### Step 8: Exception Capture (Roslyn Walk)
**Modify**: `Core/Analysis/OperationWalker.cs`

Add new internal helper class `ExceptionCapture` (either nested in OperationWalker.cs or as a new file `Core/Analysis/ExceptionCapture.cs`) to avoid growing OperationWalker further:

Track in the walk:
- **`IThrowOperation`**: capture the exception type (resolved via `INamedTypeSymbol.ToDisplayString()`), the containing method ID, and whether it is a rethrow (`thrownObject == null`)
- **`ITryOperation`**: for each `ICatchClauseOperation`, capture the caught exception type and the handler method scope

Resolve exception type hierarchy **during the walk** (while Roslyn is available): check `INamedTypeSymbol.BaseType` chain against the caught type so post-processing does not need Roslyn.

Add to `FlowGraph` (in `Models.cs`):
```csharp
[JsonPropertyName("exceptionFlows")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<ExceptionFlow>? ExceptionFlows { get; set; }
```

#### TDD

**Tests first** (`FlowUnderstandingTests.cs` — `ExceptionFlowTests` class):

These tests require small Roslyn compilation fixtures (use existing `AnalyzerEngine.AnalyzeFiles` with a minimal inline C# string, same approach used in manual smoke tests):

```
RED tests:
- Method with "throw new ArgumentException()" produces an ExceptionFlow entry on FlowGraph
- Method with try { throw } catch (ArgumentException) {} produces ExceptionFlow with catchSite set to same method
- Rethrow ("throw;") is captured and marked as rethrow
- Catch-all ("catch (Exception ex)") is matched correctly
```

Write → RED → implement → GREEN.

---

### Step 9: Exception Flow Builder
**Create**: `Core/Analysis/ExceptionFlowBuilder.cs` (`internal sealed`)

Post-processes the `ExceptionFlow` entries on FlowGraph + the call graph (from Step 6) to build a propagation picture:

1. For each throw that is not caught in its own method (no local catch, or rethrow), trace callers via the call graph
2. Find the first caller with a matching catch clause
3. Build the `propagationChain` — list of methodIds through which the exception passed uncaught
4. Mark `ExceptionFlow.CatchSite = null` and set `propagationChain` for unhandled exceptions
5. Build `List<ExceptionEdge>` connecting consecutive methods in the propagation chain

```csharp
internal sealed class ExceptionFlowBuilder
{
    public static List<ExceptionFlow> Build(
        List<AnalysisResult> results,
        CallGraph callGraph,
        Action<string>? log = null)
}
```

#### TDD

**Tests first** (add to `ExceptionFlowTests` class):

```
RED tests:
- Exception thrown in method A, caught in direct caller B: propagationChain = ["A", "B"], catchSite = B
- Exception thrown in A, not caught by B or C (transitive caller): catchSite = null, propagationChain = ["A", "B", "C"]
- Multiple throws of different exception types in same method produce separate ExceptionFlow entries
- ExceptionEdge list has one entry per hop in propagation chain
```

Write → RED → implement → GREEN.

---

### Step 10: Async Flow Capture (Roslyn Walk)
**Modify**: `Core/Analysis/OperationWalker.cs` (or new `Core/Analysis/AsyncCapture.cs`)

The walker already captures `CallNode.IsAwaited` and `MethodNode.IsAsync`. Add:

1. When `isAwaited == true` in `ProcessInvocation`, emit a new `FlowEdge` of kind `"async-continuation"` from the call's result node to the assignment target (the variable that receives the awaited value)
2. When a method returns `Task<T>` or `ValueTask<T>`, record the unwrapped inner type `T` on the `MethodNode`
3. Detect `ConfigureAwait(false)`: check if the parent invocation is chained with `.ConfigureAwait(false)` and set `isConfigureAwait` on the `AsyncFlowEdge`

Add to `FlowGraph` (in `Models.cs`):
```csharp
[JsonPropertyName("asyncFlows")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<AsyncFlowEdge>? AsyncFlows { get; set; }
```

#### TDD

**Tests first** (`FlowUnderstandingTests.cs` — `AsyncFlowTests` class):

```
RED tests:
- "var result = await SomeMethodAsync()" produces an AsyncFlowEdge entry on FlowGraph
- ConfigureAwait(false) sets isConfigureAwait = true on the edge
- Non-awaited async call (fire-and-forget) does NOT produce an AsyncFlowEdge
- Method returning Task<string> has its unwrapped type recorded as "string"
```

Write → RED → implement → GREEN.

---

### Step 11: Async Flow Integration in Traversal
**Modify**: `Core/GraphTraversal.cs`, `Core/TraversalModels.cs`

1. `"async-continuation"` edges are already `FlowEdge` instances — the edge index automatically includes them, so no indexing change is needed
2. Add `isAsyncBoundary` to `TraversalNode`:

```csharp
// In TraversalModels.cs:
[JsonPropertyName("isAsyncBoundary")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
public bool IsAsyncBoundary { get; set; }
```

3. In `GraphTraversalEngine.BuildNode`: when the edge leading to this node has kind `"async-continuation"`, set `isAsyncBoundary = true`

#### TDD

**Tests first** (add to `AsyncFlowTests` class):

```
RED tests:
- Forward traversal through an async-continuation edge produces a TraversalNode with isAsyncBoundary = true
- Traversal through a regular assignment edge does NOT set isAsyncBoundary
```

Write → RED → implement → GREEN.

---

### Step 12: HTTP Request Flow Tracer
**Create**: `Core/Analysis/HttpFlowTracer.cs` (`internal sealed`)

Composes Phase 2's `EntryPointDetector` results + Step 6's `CallGraphBuilder` + Step 4's `PathFinder`:

1. Receive `List<EntryPoint>` filtered to `controller-action` and `minimal-api` kinds
2. For each entry point, use the call graph to find all leaf nodes reachable from it
3. Identify "sink" leaf nodes by matching method names against known DB/HTTP client patterns:
   - **database**: method names containing `SaveChanges`, `ExecuteAsync`, `QueryAsync`, `ExecuteScalar`, `ExecuteNonQuery`, `ExecuteSql`, `FromSql`
   - **http-client**: `SendAsync`, `GetAsync`, `PostAsync`, `PutAsync`, `DeleteAsync`
   - **message-queue**: `PublishAsync`, `SendMessageAsync`, `ProduceAsync`
   - **file-io**: `WriteAsync`, `WriteAllText`, `WriteAllBytes`, `CopyTo`
4. For each (entry-point, sink) pair, call `PathFinder.FindPaths` to get the full request lifecycle path
5. Return `List<HttpRequestFlow>`

CLI flag: `--http-flows`

`AnalyzerEngine` addition:
```csharp
public static List<HttpRequestFlow> BuildHttpFlows(
    List<AnalysisResult> results,
    Action<string>? log = null)
```

#### TDD

**Tests first** (`FlowUnderstandingTests.cs` — `HttpFlowTracerTests` class):

```
RED tests:
- Controller action with a path to a method containing "SaveChangesAsync" produces one HttpRequestFlow with sinkKind="database"
- Entry point with no reachable sink returns empty list (not an error)
- HTTP client sink ("SendAsync") is detected and labelled correctly
- HttpRequestFlow.Path is a valid FlowPath connecting entry point to sink
```

Write → RED → implement → GREEN.

---

### Step 13: Taint Analysis Engine
**Create**: `Core/Analysis/TaintAnalyzer.cs` (`internal sealed`)

Forward propagation engine:

1. Accept a `sourceId` (the taint origin) and optional `taintLabel` (e.g., `"user-input"`, `"sql-param"`)
2. Build forward edge index from all `FlowEdge` data
3. BFS forward from `sourceId`, marking every reachable symbol as tainted
4. **Taint propagates only through data-flow edges**: `assignment`, `argument`, `return`, `field-write`, `property-write`, `initialization`, `linq-pipeline`. It does NOT propagate through `inter-method` or `inter-class` call-relationship edges (those represent call graph topology, not data transfer)
5. Track the path from `sourceId` to each tainted symbol as a `FlowPath`
6. Identify "sinks": match tainted call sites against the same sink pattern list used in `HttpFlowTracer`
7. Return `TaintResult`

CLI flags: `--taint <sourceId> [--taint-label <label>]`

`AnalyzerEngine` addition:
```csharp
public static TaintResult AnalyzeTaint(
    List<AnalysisResult> results,
    string sourceId,
    string? taintLabel = null,
    Action<string>? log = null)
```

#### TDD

**Tests first** (`FlowUnderstandingTests.cs` — `TaintAnalyzerTests` class):

```
RED tests:
- Taint from sourceId propagates through "assignment" edge to reach target
- Taint propagates through "argument" edge into a called method's parameter
- Taint does NOT propagate through "inter-method" edges (call topology, not data)
- Taint does NOT propagate through "inter-class" edges
- Taint propagates through "linq-pipeline" edges (tainted source → LINQ chain → terminal)
- Symbol matching a sink pattern (e.g., "SaveChangesAsync") in the taint reach is flagged as isSink=true
- taintLabel is surfaced on TaintResult.TaintLabel
- Source not found returns TaintResult with empty reachableSymbols
```

Write → RED → implement → GREEN.

---

### Step 14: Final CLI Integration
**Modify**: `Cli/ParsedArgs.cs`, `Cli/Program.cs`, `Core/AnalyzerEngine.cs`

Final pass to wire all remaining flags and add `BuildFlowUnderstanding` orchestrator method.

Complete `ParsedArgs` additions from Phase 3:

| Flag | Type | Default | Purpose |
|------|------|---------|---------|
| `--trace-path <src> <tgt>` | string × 2 | null | Point-to-point path query (Step 5) |
| `--call-graph` | bool | false | Extract method call graph (Step 7) |
| `--filter-ns <prefix>` | string | null | Filter call graph by namespace (Step 7) |
| `--filter-class <classId>` | string | null | Filter call graph by class (Step 7) |
| `--http-flows` | bool | false | HTTP request lifecycle tracing (Step 12) |
| `--taint <symbolId>` | string | null | Forward taint analysis (Step 13) |
| `--taint-label <label>` | string | null | Label for taint origin (Step 13) |

`AnalyzerEngine.BuildFlowUnderstanding`:
```csharp
public static FlowUnderstandingOutput BuildFlowUnderstanding(
    List<AnalysisResult> results,
    string? tracePathSource = null,
    string? tracePathTarget = null,
    bool buildCallGraph = false,
    string? filterNs = null,
    string? filterClass = null,
    bool buildHttpFlows = false,
    string? taintSourceId = null,
    string? taintLabel = null,
    Action<string>? log = null)
```

Update `PrintUsage()` in `Program.cs` with all new flags.

#### TDD

**Tests first** (add to `ParsedArgsPhase3Tests`):

```
RED tests:
- "--http-flows" sets HttpFlows=true
- "--taint SomeSymbol" sets TaintSourceId="SomeSymbol"
- "--taint SomeSymbol --taint-label user-input" sets both fields
- All flags default to their unset values when not provided
```

Write → RED → implement → GREEN.

---

### Step 15: Final Test Audit
**Review & expand**: `Tests/FlowUnderstandingTests.cs`

Walk through every test class and verify:
- All RED tests are now GREEN
- Each feature has at least one "unhappy path" test (no result, bad input)
- CLI flag parsing tests cover all new flags
- `dotnet test` shows coverage of all new classes

Minimum test count target: ~40 new tests (on top of 64 existing = ~104 total).

---

## Files Summary

| Status | Action | File | Purpose |
|--------|--------|------|---------|
| ⬜ | Create | `Core/FlowModels.cs` | All Phase 3 model types |
| ⬜ | Modify | `Core/OutputModels.cs` | Add `FlowUnderstandingOutput` to output types |
| ⬜ | Create | `Tests/FlowUnderstandingTests.cs` | Phase 3 test suite (built incrementally, step by step) |
| ⬜ | Modify | `Core/Analysis/OperationWalker.cs` | Emit `linq-pipeline` FlowEdges; add `ExceptionCapture` + `AsyncCapture` delegates |
| ⬜ | Create | `Core/Analysis/ExceptionCapture.cs` | (if extracted) throw/catch Roslyn walk helper |
| ⬜ | Create | `Core/Analysis/AsyncCapture.cs` | (if extracted) async continuation Roslyn walk helper |
| ⬜ | Create | `Core/Analysis/PathFinder.cs` | BFS point-to-point path query — no depth/path limits |
| ⬜ | Create | `Core/Analysis/CallGraphBuilder.cs` | Method-to-method call graph extraction + filters |
| ⬜ | Create | `Core/Analysis/ExceptionFlowBuilder.cs` | Exception propagation analysis using call graph |
| ⬜ | Create | `Core/Analysis/HttpFlowTracer.cs` | HTTP request lifecycle: entry point → sink |
| ⬜ | Create | `Core/Analysis/TaintAnalyzer.cs` | Forward taint propagation |
| ⬜ | Modify | `Core/Models.cs` | Add `exceptionFlows`, `asyncFlows` to FlowGraph; add `linq-pipeline` edge kind |
| ⬜ | Modify | `Core/GraphTraversal.cs` | LINQ chain edge indexing, `isAsyncBoundary` flag |
| ⬜ | Modify | `Core/TraversalModels.cs` | Add `isAsyncBoundary` to `TraversalNode` |
| ⬜ | Modify | `Core/AnalyzerEngine.cs` | Add `BuildFlowUnderstanding`, `FindPaths`, `BuildCallGraph`, `BuildHttpFlows`, `AnalyzeTaint` |
| ⬜ | Modify | `Cli/ParsedArgs.cs` | Add all Phase 3 CLI flags |
| ⬜ | Modify | `Cli/Program.cs` | Route Phase 3 output, update usage text |

---

## Step Order (Dependency Graph)

```
Step 1: FlowModels.cs (models — no tests needed)
Step 2: OutputModels.cs (structural — no tests needed)
Step 3: LINQ traversal (tests → implement) ← quick win, independent
Step 4: PathFinder.cs (tests → implement)
Step 5: CLI wire --trace-path (tests → implement)
Step 6: CallGraphBuilder (tests → implement)
Step 7: CLI wire --call-graph (tests → implement)
Step 8: Exception capture in OperationWalker (tests → implement)
Step 9: ExceptionFlowBuilder (tests → implement) ← depends on Steps 6, 8
Step 10: Async capture in OperationWalker (tests → implement)
Step 11: Async flow in traversal (tests → implement) ← depends on Step 10
Step 12: HttpFlowTracer (tests → implement) ← depends on Steps 4, 6
Step 13: TaintAnalyzer (tests → implement) ← depends on Steps 1, 4
Step 14: Final CLI integration (tests → implement)
Step 15: Final test audit
```

Recommended execution order: **1 → 2 → 3 → 4 → 5 → 6 → 7 → 8 → 9 → 10 → 11 → 12 → 13 → 14 → 15**

---

## Key Design Decisions

| Decision | Rationale |
|----------|-----------|
| **No path count or depth limits in PathFinder** | Users need to see all paths to understand the true data flow; artificial caps hide information. Cycle detection (per-path visited set) is the only termination condition. |
| **BFS for path ordering** | Shortest paths returned first; easier to read when there are many paths |
| **Exception hierarchy resolved during Roslyn walk** | Post-processing doesn't have access to Roslyn type symbols; resolve inheritance in Step 8 while the semantic model is available |
| **Taint does not propagate through call-topology edges** | `inter-method` and `inter-class` edges represent "this method calls that method," not "this data flows into that method." Propagating taint through them causes false positives |
| **ExceptionCapture / AsyncCapture as separate helpers** | `OperationWalker.cs` is already large; delegating to helpers keeps the file maintainable |

---

## Risks & Mitigations

| Risk | Mitigation |
|------|-----------|
| BFS never terminates on cyclic graphs with no depth cap | Per-path visited set: a path terminates when the next candidate node is already in that path's own chain |
| Exception type hierarchy matching | Resolved during the Roslyn walk; stored as pre-resolved "catches" data |
| Taint false positives from over-tainting | Only propagate through data-flow edge kinds; document which edge kinds are included |
| Async continuation identification is approximate | Documented as "lexical continuation" (next assignment target), not IL-level resumption. Sufficient for developer understanding |
| `Core/` has `<Restore>false</Restore>` — no new NuGet packages | All features use existing Roslyn APIs (already referenced) and BCL |

---

## Verification Checklist

| # | Command | Expected |
|---|---------|----------|
| 1 | `dotnet build` | 0 errors, 0 warnings |
| 2 | `dotnet test CSharpDataFlowAnalyzer.Tests/` | All tests pass (64 existing + ~40 new Phase 3 tests ≈ 104 total) |
| 3 | `dotnet run --project Cli/ -- TestSamples/OrderService.cs --trace-path "A" "B"` | JSON PathQueryResult — all paths shown, no artificial limit |
| 4 | `dotnet run --project Cli/ -- TestSamples/OrderService.cs --call-graph` | JSON CallGraph output |
| 5 | `dotnet run --project Cli/ -- TestSamples/OrderService.cs --call-graph --filter-ns "OrderService"` | Filtered CallGraph |
| 6 | `dotnet run --project Cli/ -- TestSamples/OrderService.cs --taint "symbolId"` | JSON TaintResult |
| 7 | `dotnet run --project Cli/ -- TestSamples/OrderService.cs --http-flows` | JSON HttpRequestFlow list |
| 8 | `dotnet run --project Cli/ -- TestSamples/OrderService.cs` | Unchanged base behavior — no regression |
| 9 | `dotnet run --project Cli/ -- TestSamples/OrderService.cs --onboard --format html -o report.html` | Unchanged onboarding — no regression |

---

## Status: PENDING IMPLEMENTATION ⬜

Phase 3 plan confirmed. Ready to begin at Step 1.
