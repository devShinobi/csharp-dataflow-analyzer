using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace CSharpDataFlowAnalyzer;

// ── Symbol registry ───────────────────────────────────────────────────────────
// Every named symbol (field, param, local, property, static) gets one entry.
// typeKind tells you whether mutations to it affect the *same* heap object.

public class SymbolInfo
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("typeKind")]
    public string TypeKind { get; set; } = "";
    // value     — int, decimal, bool, struct, enum  (copy on assign)
    // reference — class, record class, array, List<T>, ...
    // interface — unknown concrete type
    // unknown   — type not resolved

    [JsonPropertyName("symbolKind")]
    public string SymbolKind { get; set; } = "";
    // field | property | param | local | static

    [JsonPropertyName("isShared")]
    public bool IsShared { get; set; }
    // true when the symbol lives beyond one call frame:
    // fields, statics, ref/out params

    [JsonPropertyName("isNullable")]
    public bool IsNullable { get; set; }

    [JsonPropertyName("isReadonly")]
    public bool IsReadonly { get; set; }

    [JsonPropertyName("declaredInMethod")]
    public string? DeclaredInMethod { get; set; }

    // Nullable flow state from Roslyn's flow analysis.
    // "notNull" | "maybeNull" — null when not analyzed.
    [JsonPropertyName("nullableFlowState")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? NullableFlowState { get; set; }

    // Other symbol IDs that may point to the same heap object.
    // Built by tracing assignment: if local B = A and A is a reference type,
    // B is an alias of A.
    [JsonPropertyName("aliasIds")]
    public List<string> AliasIds { get; set; } = new();
}

// ── Mutation record ───────────────────────────────────────────────────────────
// One entry per write that changes observable state of a reference-type object
// or a shared symbol.  Value-type local reassignments are NOT mutations.

public class MutationNode
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";
    // property-set      obj.Prop = x
    // field-write       obj._field = x  (or direct field on this)
    // index-write       arr[i] = x
    // collection-add    list.Add(x)  / list.AddRange(...)
    // collection-remove list.Remove(x) / list.RemoveAt(i) / list.Clear()
    // collection-sort   list.Sort() / list.Reverse()
    // external-call     call on a reference receiver that *may* mutate it
    // ref-write         value written back via ref/out param

    [JsonPropertyName("targetSymbolId")]
    public string TargetSymbolId { get; set; } = "";
    // The symbol whose heap object is being changed

    [JsonPropertyName("targetMember")]
    public string? TargetMember { get; set; }
    // Which property/field/index is being written (null for whole-object mutations)

    [JsonPropertyName("sourceExpression")]
    public string SourceExpression { get; set; } = "";

    [JsonPropertyName("sourceIds")]
    public List<string> SourceIds { get; set; } = new();

    [JsonPropertyName("methodId")]
    public string MethodId { get; set; } = "";

    [JsonPropertyName("isOnSharedObject")]
    public bool IsOnSharedObject { get; set; }
    // true when targetSymbolId is a field or static — mutation is visible class-wide

    [JsonPropertyName("isInsideLoop")]
    public bool IsInsideLoop { get; set; }
    // true when this mutation occurs inside a for/foreach/while body

    // The condition that gates this mutation.  null = unconditional (always runs).
    [JsonPropertyName("guard")]
    public ConditionGuard? Guard { get; set; }

    // For aliased mutations: all symbol IDs that point to the same object
    [JsonPropertyName("affectedAliasIds")]
    public List<string> AffectedAliasIds { get; set; } = new();
}

// ── Condition guard ───────────────────────────────────────────────────────────
// The boolean context in which a mutation occurs.

public class ConditionGuard
{
    [JsonPropertyName("expression")]
    public string Expression { get; set; } = "";

    [JsonPropertyName("branch")]
    public string Branch { get; set; } = "";
    // then | else | catch | finally | loop-body | case

    [JsonPropertyName("conditionKind")]
    public string ConditionKind { get; set; } = "";
    // if | switch-case | try-catch | try-finally | foreach | for | while | null-check

    [JsonPropertyName("isNullCheck")]
    public bool IsNullCheck { get; set; }

    [JsonPropertyName("isNegated")]
    public bool IsNegated { get; set; }

    [JsonPropertyName("involvedSymbolIds")]
    public List<string> InvolvedSymbolIds { get; set; } = new();
}

// ── State change summary ──────────────────────────────────────────────────────
// Per-symbol rollup: what can change, from where, under what conditions.

public class StateChangeSummary
{
    [JsonPropertyName("symbolId")]
    public string SymbolId { get; set; } = "";

    [JsonPropertyName("symbolName")]
    public string SymbolName { get; set; } = "";

    [JsonPropertyName("typeName")]
    public string TypeName { get; set; } = "";

    [JsonPropertyName("typeKind")]
    public string TypeKind { get; set; } = "";

    [JsonPropertyName("isShared")]
    public bool IsShared { get; set; }

    // Every mutation that touches this symbol
    [JsonPropertyName("mutations")]
    public List<MutationRef> Mutations { get; set; } = new();

    // Which methods write to this symbol
    [JsonPropertyName("writtenFromMethods")]
    public List<string> WrittenFromMethods { get; set; } = new();

    // Which methods read this symbol (as a source in any flow edge)
    [JsonPropertyName("readFromMethods")]
    public List<string> ReadFromMethods { get; set; } = new();

    // true if ANY mutation has a guard (state change is conditional)
    [JsonPropertyName("hasConditionalMutation")]
    public bool HasConditionalMutation { get; set; }

    // true if mutations originate from more than one method
    [JsonPropertyName("isWrittenFromMultipleMethods")]
    public bool IsWrittenFromMultipleMethods { get; set; }

    // true if any mutation occurs inside a loop
    [JsonPropertyName("hasMutationInLoop")]
    public bool HasMutationInLoop { get; set; }

    // true if any alias of this symbol is passed to an external call
    [JsonPropertyName("hasAliasedExternalCall")]
    public bool HasAliasedExternalCall { get; set; }
}

public class MutationRef
{
    [JsonPropertyName("mutationId")]
    public string MutationId { get; set; } = "";

    [JsonPropertyName("methodId")]
    public string MethodId { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "";

    [JsonPropertyName("member")]
    public string? Member { get; set; }

    [JsonPropertyName("guardExpression")]
    public string? GuardExpression { get; set; }

    [JsonPropertyName("guardBranch")]
    public string? GuardBranch { get; set; }

    [JsonPropertyName("isInsideLoop")]
    public bool IsInsideLoop { get; set; }
}

// ── Top-level mutation graph ──────────────────────────────────────────────────

public class MutationGraph
{
    [JsonPropertyName("symbols")]
    public List<SymbolInfo> Symbols { get; set; } = new();

    [JsonPropertyName("mutations")]
    public List<MutationNode> Mutations { get; set; } = new();

    [JsonPropertyName("stateChangeSummaries")]
    public List<StateChangeSummary> StateChangeSummaries { get; set; } = new();

    // redFlags intentionally omitted — added later as a separate rule engine
}
