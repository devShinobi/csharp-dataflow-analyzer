# Phase 2 — Onboarding: Codebase Understanding

## Context

Phase 1 (Solution-Scale Analysis) is complete — the tool can now load `.sln`/`.csproj` files, resolve NuGet references, and analyze multi-project codebases. Phase 2 builds on that foundation to answer: **"I just joined this team — show me the architecture, entry points, and the 10 most important classes."**

All Phase 2 features are **post-processing** over the existing `AnalysisResult[]` output. No changes to the core Roslyn walk are needed except one small addition: capturing class/method attributes (needed for entry point detection).

---

## Implementation Steps

### Step 1: Attribute Capture (prerequisite)
**Modify**: `Core/Models.cs`, `Core/Analysis/OperationWalker.cs`

Add optional `Attributes` list to `ClassUnit` and `MethodNode` (~10 lines in OperationWalker — read `GetAttributes()` from `INamedTypeSymbol` and `IMethodSymbol`, store as display strings).

```csharp
// ClassUnit + MethodNode gain:
[JsonPropertyName("attributes")]
[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
public List<string>? Attributes { get; set; }
```

---

### Step 2: Onboarding Models
**Create**: `Core/OnboardingModels.cs`

All new model types in one file, following existing pattern (mutable `{ get; set; }`, `[JsonPropertyName]`, `JsonIgnoreCondition.WhenWritingNull`):

| Model | Purpose |
|-------|---------|
| `DependencyGraph` | Three-level deps: project, namespace, class |
| `DependencyEdge` | from, to, kind (inheritance/interface-impl/field-type/constructor-param/method-call/return-type), weight |
| `ClassRelationship` | Per-class: dependsOn, dependedOnBy, constructorParams, interfaces, baseClass, fanIn, fanOut |
| `EntryPoint` | classId, methodId, kind (main/controller-action/minimal-api/background-service/mediatr-handler), httpMethod?, route? |
| `HotNode` | classId, fanIn, fanOut, totalConnections, rank, role (hub/provider/consumer/leaf) |
| `ClassExplanation` | responsibilities, dependencies, state, publicApi, keyFlows, isEntryPoint, hotRank |
| `OnboardingOutput` | Top-level wrapper: dependencyGraph, entryPoints, classRelationships, hotNodes, classExplanation?, results |

---

### Step 3: Dependency Analyzer
**Create**: `Core/Analysis/DependencyAnalyzer.cs` (`internal sealed`)

Post-processes `List<AnalysisResult>` → `DependencyGraph` + `List<ClassRelationship>`:
1. Build classId → ClassUnit lookup across all results
2. Build type-name → classId map from namespace + name
3. For each ClassUnit: extract edges from baseTypes (inheritance/interface), constructor params (DI), field/property types (composition), inter-class FlowEdges (method-call)
4. Aggregate to namespace-level (extract namespace from classId) and project-level (from AnalysisResult.Source path)
5. Compute fan-in/fan-out per class

---

### Step 4: Entry Point Detector
**Create**: `Core/Analysis/EntryPointDetector.cs` (`internal sealed`)

Heuristic-based detection from ClassUnit data + attributes:
- **main**: Method named `Main` or `<Main>$`
- **controller-action**: BaseTypes contains `ControllerBase`/`Controller`, or `[ApiController]` attribute; methods with `[HttpGet]`/`[HttpPost]`/etc.
- **minimal-api**: CallNode.MethodName in `{MapGet, MapPost, MapPut, MapDelete, MapPatch}`
- **background-service**: BaseTypes contains `BackgroundService` or `IHostedService`
- **mediatr-handler**: BaseTypes matches `IRequestHandler<` or `INotificationHandler<`

---

### Step 5: Hot Path Detector
**Create**: `Core/Analysis/HotPathDetector.cs` (`internal sealed`)

Simple: sort `ClassRelationship` by `fanIn + fanOut` descending, return top N (default 10). Classify role:
- `hub`: high fan-in AND high fan-out (above median for both)
- `provider`: high fan-out, low fan-in
- `consumer`: high fan-in, low fan-out
- `leaf`: low both

---

### Step 6: Class Explainer
**Create**: `Core/Analysis/ClassExplainer.cs` (`internal sealed`)

Given a classId, assembles `ClassExplanation` from Steps 3-5:
- **Responsibilities**: inferred from attributes, base types, state shape (e.g. "Handles HTTP requests", "Manages stateful data", "Utility class")
- **State**: mutable fields vs readonly, properties
- **Public API**: public methods with param count, return type, callsOut count
- **Key flows**: follow inter-method edges from each public method 2-3 hops

---

### Step 7: Wire Orchestration
**Modify**: `Core/AnalyzerEngine.cs`

New public method:
```csharp
public static OnboardingOutput BuildOnboarding(
    List<AnalysisResult> results,
    string? explainClassId = null,
    Action<string>? log = null)
```
Calls Steps 3 → 4 → 5 → 6 in sequence, assembles `OnboardingOutput`.

---

### Step 8: CLI Integration
**Modify**: `Cli/ParsedArgs.cs`, `Cli/Program.cs`

New flags:
- `--onboard` — produce full onboarding report
- `--explain <classId>` — single-class deep dive (implies onboard)
- `--format html` — HTML output instead of JSON (default: `json`)

ParsedArgs gains: `bool Onboard`, `string? ExplainClassId`, `string Format = "json"`.

---

### Step 9: HTML Report Generator
**Create**: `Core/HtmlReportGenerator.cs` (`public static`)

Self-contained HTML with embedded CSS + minimal vanilla JS:
- **Sections**: Summary stats → Entry Points → Architecture Map (namespace-grouped) → Hot Nodes table → Class Index (collapsible `<details>/<summary>`)
- **Interactivity**: expand/collapse all, filter-by-namespace, `#classId` anchor links
- **No external deps**: inline `<style>` + `<script>`, monospace aesthetic
- **Large codebase guard**: default namespace-level summary, class detail for top 50 by hot rank

---

### Step 10: Tests
**Create**: `Tests/OnboardingTests.cs`

Test with Fixture-style builders (same pattern as existing tests):
- `DependencyAnalyzerTests`: verify edges extracted from mock FlowGraph/ClassUnits
- `EntryPointDetectorTests`: verify each heuristic (controller, main, background service)
- `HotPathDetectorTests`: verify ranking and role classification
- `ClassExplainerTests`: verify responsibility inference
- `ParsedArgsTests`: verify new flag parsing

---

## Files Summary

| Action | File | Purpose |
|--------|------|---------|
| Modify | `Core/Models.cs` | Add `Attributes` to ClassUnit, MethodNode |
| Modify | `Core/Analysis/OperationWalker.cs` | Capture GetAttributes() |
| Create | `Core/OnboardingModels.cs` | All Phase 2 model types |
| Create | `Core/Analysis/DependencyAnalyzer.cs` | Build dependency graph |
| Create | `Core/Analysis/EntryPointDetector.cs` | Detect entry points |
| Create | `Core/Analysis/HotPathDetector.cs` | Rank hot nodes |
| Create | `Core/Analysis/ClassExplainer.cs` | Explain a class |
| Modify | `Core/AnalyzerEngine.cs` | Add BuildOnboarding() |
| Modify | `Cli/ParsedArgs.cs` | Add --onboard, --explain, --format |
| Modify | `Cli/Program.cs` | Route onboarding output |
| Create | `Core/HtmlReportGenerator.cs` | Generate HTML report |
| Create | `Tests/OnboardingTests.cs` | Phase 2 test suite |

---

## Verification

1. `dotnet build` — all projects compile
2. `dotnet test` — existing 42 tests still pass + new onboarding tests pass
3. `dotnet run --project Cli/ -- TestSamples/OrderService.cs --onboard` — JSON onboarding output
4. `dotnet run --project Cli/ -- C:/tmp/eShop/eShop.sln --onboard --format html -o report.html` — full HTML report on eShop
5. `dotnet run --project Cli/ -- C:/tmp/eShop/eShop.sln --explain "OrderServices"` — single-class explanation
