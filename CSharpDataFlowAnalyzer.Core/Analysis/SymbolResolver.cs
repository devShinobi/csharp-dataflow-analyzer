using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Maps Roslyn <see cref="ISymbol"/> instances to the stable human-readable IDs
/// produced by <see cref="IdGen"/>.  Maintains per-method counters for overloads,
/// local declaration indices, assignment indices, and call indices so that IDs
/// are deterministic across runs.
/// </summary>
internal sealed class SymbolResolver
{
    // ── Class / method context ───────────────────────────────────────────────

    private string _currentClassId = "";
    private string _currentMethodId = "";

    // ── Overload tracking: (classId, methodName) → next index ────────────────
    private readonly Dictionary<(string classId, string name), int> _overloadIndex = new();
    private int _ctorIndex;

    // ── Per-method counters (reset when method changes) ──────────────────────
    private readonly Dictionary<string, int> _localDeclIndex = new();
    private readonly Dictionary<string, int> _assignIndex = new();
    private readonly Dictionary<string, int> _callIndex = new();
    private int _returnIndex;

    // ── Reverse lookup: ISymbol → resolved ID ────────────────────────────────
    private readonly Dictionary<ISymbol, string> _symbolIds = new(SymbolEqualityComparer.Default);

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>Sets the current class context. Call before processing a type's members.</summary>
    internal string EnterClass(string ns, string className)
    {
        _currentClassId = IdGen.Class(ns, className);
        _ctorIndex = 0;
        return _currentClassId;
    }

    /// <summary>Sets the current method context. Call before processing a method body.</summary>
    internal string EnterMethod(string methodName)
    {
        var key = (_currentClassId, methodName);
        _overloadIndex.TryGetValue(key, out int idx);
        _currentMethodId = IdGen.Method(_currentClassId, methodName, idx);
        _overloadIndex[key] = idx + 1;
        ResetMethodCounters();
        return _currentMethodId;
    }

    /// <summary>Sets the current constructor context.</summary>
    internal string EnterConstructor()
    {
        _currentMethodId = IdGen.Constructor(_currentClassId, _ctorIndex++);
        ResetMethodCounters();
        return _currentMethodId;
    }

    internal string CurrentClassId => _currentClassId;
    internal string CurrentMethodId => _currentMethodId;

    // ── Symbol → ID resolution ───────────────────────────────────────────────

    internal string ResolveField(IFieldSymbol field)
    {
        var id = IdGen.Field(_currentClassId, field.Name);
        _symbolIds[field] = id;
        return id;
    }

    internal string ResolveProperty(IPropertySymbol prop)
    {
        var id = IdGen.Property(_currentClassId, prop.Name);
        _symbolIds[prop] = id;
        return id;
    }

    internal string ResolveParam(IParameterSymbol param)
    {
        var id = IdGen.Param(_currentMethodId, param.Name);
        _symbolIds[param] = id;
        return id;
    }

    internal string ResolveLocal(ILocalSymbol local)
    {
        _localDeclIndex.TryGetValue(local.Name, out int idx);
        var id = IdGen.Local(_currentMethodId, local.Name, idx);
        _localDeclIndex[local.Name] = idx + 1;
        _symbolIds[local] = id;
        return id;
    }

    internal string NextAssignmentId(string target)
    {
        _assignIndex.TryGetValue(target, out int idx);
        _assignIndex[target] = idx + 1;
        return IdGen.Assignment(_currentMethodId, target, idx);
    }

    internal string NextCallId(string callExpr)
    {
        _callIndex.TryGetValue(callExpr, out int idx);
        _callIndex[callExpr] = idx + 1;
        return IdGen.Call(_currentMethodId, callExpr, idx);
    }

    internal string NextReturnId()
        => IdGen.Return(_currentMethodId, _returnIndex++);

    /// <summary>
    /// Looks up a previously registered symbol's ID.  Returns null if not registered.
    /// </summary>
    internal string? Lookup(ISymbol? symbol)
    {
        if (symbol == null) return null;
        return _symbolIds.TryGetValue(symbol, out var id) ? id : null;
    }

    /// <summary>
    /// Resolves any symbol to its ID, registering it if necessary.
    /// Falls back to null for unrecognized symbols.
    /// </summary>
    internal string? ResolveAny(ISymbol? symbol) => symbol switch
    {
        null => null,
        IFieldSymbol f => Lookup(f) ?? ResolveField(f),
        IPropertySymbol p => Lookup(p) ?? ResolveProperty(p),
        IParameterSymbol p => Lookup(p) ?? ResolveParam(p),
        ILocalSymbol l => Lookup(l) ?? ResolveLocal(l),
        _ => Lookup(symbol)
    };

    // ── Private ──────────────────────────────────────────────────────────────

    private void ResetMethodCounters()
    {
        _localDeclIndex.Clear();
        _assignIndex.Clear();
        _callIndex.Clear();
        _returnIndex = 0;
    }
}
