# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build entire solution
dotnet build

# Run analysis on a single file
dotnet run --project CSharpDataFlowAnalyzer.Cli/ -- <file.cs> [options]

# Run tests
dotnet test CSharpDataFlowAnalyzer.Tests/

# Options
#   -o / --output <path>         Write JSON output to a file instead of stdout
#   --compact                    Minified JSON output
#   --trace-forward  <symbolId>  Forward traversal (what does X affect?)
#   --trace-backward <symbolId>  Backward traversal (what feeds into X?)
#   --trace-depth    <n>         Max traversal depth (default 20)

# Example
dotnet run --project CSharpDataFlowAnalyzer.Cli/ -- TestSamples/OrderService.cs -o analysis.json --compact
```

**Note**: `CSharpDataFlowAnalyzer.Core` and `CSharpDataFlowAnalyzer.Cli` have `<Restore>false</Restore>` — their only dependency is Roslyn, referenced via HintPath from `C:\Program Files\dotnet\sdk\10.0.201\Roslyn\bincore\`. Do not run `dotnet restore` on those projects. The test project restores normally from nuget.org.

## Project Structure

```
CSharpDataFlowAnalyzer.sln
├── CSharpDataFlowAnalyzer.Core/     # Class library — analysis pipeline + models
│   ├── Analysis/
│   │   ├── OperationWalker.cs       # Unified IOperation walker → FlowGraph + MutationGraph
│   │   ├── UnifiedAnalyzer.cs       # Entry point facade for the unified pipeline
│   │   ├── TypeClassifier.cs        # ITypeSymbol → typeKind/isNullable classification
│   │   ├── SymbolResolver.cs        # ISymbol → stable IdGen ID mapping
│   │   └── ControlFlowMapper.cs     # CFG regions → ConditionGuard + loop detection
│   ├── AnalyzerEngine.cs            # Public API: CollectFiles, AnalyzeFiles, BuildOutput
│   ├── GraphTraversal.cs            # GraphTraversalEngine (forward/backward DFS)
│   ├── IdGen.cs                     # Stable human-readable node ID generation
│   ├── Models.cs                    # FlowGraph model (ClassUnit, MethodNode, FlowEdge …)
│   ├── MutationModels.cs            # MutationGraph model (SymbolInfo, MutationNode …)
│   ├── OutputModels.cs              # AnalysisResult, MultiFileOutput
│   └── TraversalModels.cs           # TraversalResult, TraversalNode, TraversalDirection
├── CSharpDataFlowAnalyzer.Cli/      # Console app — thin CLI orchestrator
│   ├── ParsedArgs.cs                # Typed CLI argument record + Parse()
│   └── Program.cs                   # Main: parse → collect → analyze → serialize
├── CSharpDataFlowAnalyzer.Tests/    # xUnit tests (42 tests)
│   └── GraphTraversalEngineTests.cs
└── TestSamples/
    └── OrderService.cs              # Sample for manual smoke testing
```

The **CLI has no Roslyn dependency** — `AnalyzerEngine.AnalyzeFiles` is the boundary; Roslyn types stay inside Core.

## Architecture

### Analysis Pipeline

```
C# Source Files
  → Roslyn SyntaxTree + SemanticModel  (AnalyzerEngine.BuildCompilation — internal)
  → UnifiedAnalyzer (single pass via IOperation tree + ControlFlowGraph)
    → OperationWalker           → FlowGraph + MutationGraph (simultaneously)
      uses: TypeClassifier      — ITypeSymbol-based type classification
      uses: SymbolResolver      — ISymbol → IdGen stable ID mapping
      uses: ControlFlowMapper   — CFG regions → ConditionGuard + loop detection
  → GraphTraversalEngine       → TraversalResult  (optional, when --trace-* is used)
  → JSON Output
```

### ID Scheme (IdGen.cs)

All graph nodes have stable IDs used as cross-references throughout both graphs:

```
Class:       "Namespace.ClassName"
Field:       "Class::field:FieldName"
Property:    "Class::prop:PropName"
Method:      "Class::MethodName[overloadIdx]"
Constructor: "Class::ctor[overloadIdx]"
Param:       "MethodId/param:ParamName"
Local:       "MethodId/local:LocalName"  (or with #declIdx for shadowed redeclarations)
Assignment:  "MethodId/assign:Target[idx]"
Call:        "MethodId/call:CallExpr[idx]"
Return:      "MethodId/return[idx]"
```

### FlowGraph (Models.cs)

Three levels of data flow:
- **Intra-method**: local variables, parameters, assignments, calls, returns
- **Inter-method**: call graph edges, argument→parameter mappings, return→result
- **Inter-class**: field reads/writes, property access, constructor DI patterns

FlowEdge kinds: `assignment`, `argument`, `return`, `field-write`, `field-read`, `property-read`, `property-write`, `inter-method`, `inter-class`, `initialization`

### MutationGraph (MutationModels.cs)

- **SymbolInfo**: every named symbol — type kind (`value`/`reference`/`interface`/`unknown`), sharing scope (`local`/`shared`), alias IDs
- **MutationNode**: single write with target, source, `ConditionGuard`, loop flag
- **ConditionGuard**: enclosing control flow (`if`, `switch-case`, `try-catch`, `foreach`, `for`, `while`, `null-check`) + branch direction
- **StateChangeSummary**: per-symbol rollup

Only reference-type symbols are treated as mutations. Value-type assignments appear in FlowGraph only.

### Output Schema

Single file → `AnalysisResult`. Multiple files → `AnalysisResult[]`. With `--trace-*` and multiple files → `MultiFileOutput`.

```json
{
  "source": "path/to/file.cs",
  "flowGraph": { "units": [], "flowEdges": [] },
  "mutationGraph": { "symbols": [], "mutations": [], "stateChangeSummaries": [] },
  "traversal": { "originSymbolId": "...", "direction": "forward", "root": {} }
}
```

## Conventions

### Naming & Visibility

- Public API lives in `Core` and uses `public` (e.g. `AnalyzerEngine`, model classes).
- Internal analysis helpers use `internal sealed` (e.g. `OperationWalker`, `SymbolResolver`, `ControlFlowMapper`, `UnifiedAnalyzer`).
- Private fields are `_camelCase`; local variables and parameters are `camelCase`; types and methods are `PascalCase`.
- The CLI project references Core but adds **no Roslyn dependency** — all Roslyn types must stay inside Core.

### Logging / Diagnostics

All public Core methods accept an optional `Action<string>? log = null` parameter that defaults to `Console.Error.WriteLine`. This keeps the API decoupled from any specific logging framework.

```csharp
// Caller can inject a logger:
engine.AnalyzeFiles(files, msg => myLogger.Info(msg));
// Or rely on default stderr output.
```

### Model Classes

- Output model classes (`FlowGraph`, `ClassUnit`, `MutationNode`, etc.) use mutable `{ get; set; }` with `= new()` collection initializers so callers can build graphs incrementally.
- CLI arg types use `sealed record` (immutable, value-based equality).
- Do not add Roslyn-specific types to any model or output class.

## Testing

Tests are in `CSharpDataFlowAnalyzer.Tests/` using **xUnit 2.9.3**.

- Tests exercise `GraphTraversalEngine`, `ParsedArgs`, and `AnalyzerEngine.BuildOutput` — all without a live Roslyn compilation.
- A `file static class Fixture` inside the test file provides builder helpers (`Fixture.Graph(...)`, `Fixture.Edge(...)`, `Fixture.Mutation(...)`) to construct minimal `FlowGraph`/`MutationGraph` fixtures.
- Test classes are grouped by feature: `ForwardTraversalTests`, `BackwardTraversalTests`, `CycleDetectionTests`, `MutationAttachmentTests`, etc.
- End-to-end analysis tests (parsing real `.cs` files) go in `TestSamples/` and are exercised manually via `dotnet run`.

```bash
dotnet test CSharpDataFlowAnalyzer.Tests/          # run all tests
dotnet test CSharpDataFlowAnalyzer.Tests/ --logger "console;verbosity=normal"
```
