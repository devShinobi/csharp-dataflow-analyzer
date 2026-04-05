# CSharp DataFlow Analyzer — Product Roadmap

## Current State (v0.1 — Foundation)

What exists today:
- Roslyn-based single-pass analysis (IOperation + CFG)
- FlowGraph: intra-method, inter-method, inter-class data flow edges
- MutationGraph: symbol registry, mutations with guards/loops/aliases
- Graph traversal engine (forward/backward DFS with cycle detection)
- CLI with JSON output, trace queries by symbol ID
- 42 xUnit tests covering traversal, parsing, output

---

## Phase 1 — Solution-Scale Analysis
**Goal: Handle real-world codebases, not just single files**

| Feature | Why | Effort |
|---------|-----|--------|
| **Solution/project file support** | Real codebases use `.sln`/`.csproj` — currently only loose `.cs` files work | M |
| **NuGet dependency resolution** | `BuildCompilation` only refs runtime BCL; user code referencing NuGet packages gets unresolved symbols → broken edges | L |
| **Cross-project references** | Multi-project solutions need merged compilation or project-aware analysis | M |
| **Incremental analysis** | Re-analyzing unchanged files is wasteful; cache per-file results keyed by content hash | M |
| **Parallel file analysis** | `Analyze()` is sequential per syntax tree; parallelize across files | S |

**Unlocks:** Ability to point the tool at any real `.sln` and get complete graphs.

---

## Phase 2 — Onboarding: Codebase Understanding
**Goal: A new developer points at a codebase and gets a mental model**

| Feature | Why | Effort |
|---------|-----|--------|
| **Dependency graph / architecture map** | Show project → project, namespace → namespace, class → class dependency layers | M |
| **Entry point detection** | Identify `Main`, ASP.NET controllers, background services, message handlers — "where does code start?" | S |
| **Class relationship summary** | For each class: what it depends on, what depends on it, DI constructor params, interface implementations | S |
| **"Explain this class" query** | Given a class ID, produce a structured summary: responsibilities, dependencies, state, public API, key flows | M |
| **Hot path detection** | Identify the most-connected nodes (high fan-in/fan-out) — these are the core abstractions newcomers should learn first | S |
| **HTML/interactive report** | JSON is powerful but not human-friendly; generate a browsable HTML report with collapsible sections and hyperlinked IDs | L |

**Unlocks:** "I just joined this team — show me the architecture, entry points, and the 10 most important classes."

---

## Phase 3 — Flow Understanding: "How does data get from A to B?"
**Goal: Answer path queries between any two points in the codebase**

| Feature | Why | Effort |
|---------|-----|--------|
| **Point-to-point path query** | `--trace-path <sourceId> <targetId>` — find all paths between two symbols | M |
| **HTTP request flow tracing** | From controller action → service → repository → DB call: trace a full request lifecycle | L |
| **Async/await flow tracking** | Current traversal doesn't model Task continuation chains or `await` resumption points | M |
| **Exception flow tracking** | Track which methods throw, which catch, and where exceptions propagate unhandled | M |
| **LINQ data pipeline tracing** | `LinqChains` exist in the model but aren't connected to traversal — follow data through `.Where().Select().ToList()` | S |
| **Call graph visualization** | Directed graph of method → method calls, filterable by namespace/class | M |
| **Taint analysis** | Mark an input as "tainted" (e.g., user input) and trace where it flows — useful for security and understanding trust boundaries | L |

**Unlocks:** "When a user clicks 'Place Order', show me every method, every data transformation, every DB write that happens."

---

## Phase 4 — Root Cause Analysis: "Why is this broken?"
**Goal: Given a symptom, narrow down where the bug lives**

| Feature | Why | Effort |
|---------|-----|--------|
| **Red flags / risk engine** | Model in `MutationModels.cs` has a placeholder for `redFlags` — implement rules: shared mutation without lock, nullable deref, write-after-read race patterns | M |
| **"Who mutates this?" query** | Given a field/property, show every method that writes it, under what conditions, and whether it's aliased | S (partially exists) |
| **Null flow analysis** | `SymbolInfo.NullableFlowState` is captured but not surfaced in traversal — show where nulls originate and propagate | M |
| **Conditional path analysis** | "This mutation only happens in the `else` branch of a null check" — surface guard chains for a given mutation | S |
| **Side effect summary per method** | For each method: what shared state does it read? Write? What external calls does it make? | M |
| **Change impact analysis** | Given a modified symbol, show all downstream consumers and upstream producers — "if I change this, what breaks?" | M |
| **Diff-aware analysis** | Accept a git diff and analyze only the changed methods, showing their upstream/downstream impact | L |

**Unlocks:** "Production is returning null for `order.Customer.Name` — show me every code path that sets `Customer` and which ones can leave it null."

---

## Phase 5 — Developer Experience
**Goal: Make it easy and pleasant to use daily**

| Feature | Why | Effort |
|---------|-----|--------|
| **VS Code extension** | Inline flow highlights, hover-to-see-mutations, right-click "trace forward/backward" | XL |
| **Watch mode** | Re-analyze on file save, update report incrementally | M |
| **Natural language queries** | "What happens when CreateOrder is called?" → translate to trace query → render result | L |
| **Mermaid/Graphviz diagram export** | Auto-generate visual flow diagrams from traversal results | M |
| **Configurable output filters** | Filter by namespace, class, edge kind, mutation kind — reduce noise for large codebases | S |
| **SARIF output** | Standard format for IDE integration (VS, VS Code, GitHub Code Scanning) | M |

---

## Prioritization

```
Now  ──────────────────────────────────────────────────── Later

Phase 1          Phase 2          Phase 3         Phase 4         Phase 5
(Solution-scale) (Onboarding)     (Flow queries)  (Root cause)    (DX)

 ┌─────────┐     ┌───────────┐    ┌───────────┐   ┌──────────┐   ┌─────────┐
 │.sln     │     │Entry pts  │    │Point-to-  │   │Red flags │   │VS Code  │
 │support  │────▶│Arch map   │───▶│point path │──▶│Null flow │──▶│extension│
 │NuGet    │     │Hot paths  │    │Request    │   │Impact    │   │Diagrams │
 │resolve  │     │HTML report│    │tracing    │   │analysis  │   │NL query │
 └─────────┘     └───────────┘    └───────────┘   └──────────┘   └─────────┘
```

Phase 1 is the prerequisite — without solution-scale analysis, nothing else works on real codebases. Phase 2 and 3 can partially overlap since they share the same graph infrastructure.
