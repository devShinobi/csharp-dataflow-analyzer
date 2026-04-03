# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build

# Run analysis on a single file
dotnet run <file.cs> [options]

# Run analysis on a directory
dotnet run <directory> [options]

# Options
#   -o / --output <path>   Write JSON output to a file instead of stdout
#   --compact              Minified JSON output

# Example
dotnet run TestSamples/OrderService.cs -o analysis.json --compact
```

**Note**: `<Restore>false</Restore>` is set in the project — dependencies come from the local .NET SDK Roslyn assemblies (`C:\Program Files\dotnet\sdk\10.0.201\Roslyn\bincore\`), not NuGet. Do not attempt `dotnet restore`.

## Architecture

This is a **two-pass static analysis tool** that produces JSON data flow and mutation graphs from C# source code using the Roslyn compiler API.

### Analysis Pipeline

```
C# Source Files
  → Roslyn SyntaxTree + SemanticModel
  → Pass 1: DataFlowWalker     → FlowGraph
  → FlowEnricher               → Enhanced FlowGraph
  → Pass 2: MutationWalker     → MutationGraph
  → JSON Output
```

### Core Components

| File | Role |
|------|------|
| [Program.cs](Program.cs) | CLI entry point; orchestrates both passes; emits JSON |
| [DataFlowWalker.cs](DataFlowWalker.cs) | AST walker; builds FlowGraph (classes, methods, assignments, calls, edges) |
| [FlowEnricher.cs](FlowEnricher.cs) | Post-processing: LINQ chain grouping, conditional init tagging, object initializer decomposition |
| [MutationWalker.cs](MutationWalker.cs) | Detects mutations with guard conditions and alias tracking |
| [Models.cs](Models.cs) | FlowGraph data model (ClassUnit, MethodNode, CallNode, FlowEdge) |
| [MutationModels.cs](MutationModels.cs) | MutationGraph model (SymbolInfo, MutationNode, ConditionGuard, StateChangeSummary) |
| [IdGen.cs](IdGen.cs) | Stable human-readable node ID generation |

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

Tracks three levels of data flow:
- **Intra-method**: local variables, parameters, assignments, calls, returns
- **Inter-method**: call graph edges, argument→parameter mappings, return→result
- **Inter-class**: field reads/writes, property access, constructor DI patterns

FlowEdge kinds: `assignment`, `argument`, `return`, `field-write`, `field-read`, `property-read`, `property-write`, `inter-method`, `inter-class`, `initialization`

### MutationGraph (MutationModels.cs)

Tracks which symbols are mutated, under what conditions, and whether they are aliased:
- **SymbolInfo**: classifies every named symbol by type kind (`value`/`reference`/`interface`/`unknown`) and sharing scope (`local`/`shared`)
- **MutationNode**: a single write operation with target, source, and a `ConditionGuard`
- **ConditionGuard**: captures the enclosing control flow (`if`, `switch-case`, `try-catch`, `foreach`, `for`, `while`, `null-check`) and branch direction
- **StateChangeSummary**: per-symbol rollup of all mutations, reading/writing methods, loop mutations, and aliased external calls

Only reference-type symbols are treated as true mutations. Value-type assignments are tracked in the FlowGraph but not the MutationGraph.

### Type Classification Heuristics (MutationWalker.cs)

When Roslyn semantic info is unavailable, types are classified by:
- Known primitives and value types (`int`, `bool`, `DateTime`, `Guid`, etc.)
- Known collections (`List<T>`, `Dictionary<K,V>`, arrays)
- Interface detection: name starts with `I` + uppercase letter
- Service heuristics: suffix is `Service`, `Repository`, `Manager`, `Handler`, `Client`, etc.
- `Task<T>` treated as reference type

### Output Schema

Single file → one JSON object. Multiple files → JSON array.

```json
{
  "source": "path/to/file.cs",
  "flowGraph": {
    "source": "...",
    "analysisDepth": "method+inter-method+inter-class",
    "units": [],
    "flowEdges": []
  },
  "mutationGraph": {
    "symbols": [],
    "mutations": [],
    "stateChangeSummaries": []
  }
}
```

## Test Sample

[TestSamples/OrderService.cs](TestSamples/OrderService.cs) is the primary sample for manual testing — an e-commerce `OrderService` exercising async methods, LINQ chains, object initializers, DI constructor injection, null guards, and foreach loops.
