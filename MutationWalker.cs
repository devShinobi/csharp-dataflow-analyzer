using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Walks the Roslyn AST to build the MutationGraph:
///   1. Classifies every symbol (value/reference/interface, shared/local)
///   2. Detects alias relationships between reference-type symbols
///   3. Records every mutation with its kind, target member, source, and condition guard
///   4. Rolls up per-symbol StateChangeSummaries
/// </summary>
public class MutationWalker
{
    private readonly SemanticModel _sem;
    private readonly FlowGraph _flow;
    private readonly MutationGraph _mg = new();

    // symbol-id → SymbolInfo for fast lookup
    private readonly Dictionary<string, SymbolInfo> _symById = new();

    // (className, symbolName) → symbol id — for resolving member accesses
    private readonly Dictionary<(string cls, string name), string> _memberIndex = new();

    private static readonly HashSet<string> ValueTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "int","long","short","byte","sbyte","uint","ulong","ushort",
        "float","double","decimal","bool","char",
        "DateTime","DateTimeOffset","TimeSpan","Guid",
        "int?","long?","decimal?","bool?","DateTime?","Guid?","double?","float?"
    };

    private static readonly HashSet<string> CollectionMutators = new(StringComparer.Ordinal)
    {
        "Add","AddRange","TryAdd","Insert","InsertRange",
        "Remove","RemoveAt","RemoveAll","RemoveRange","RemoveWhere","TryRemove",
        "Clear","Push","Pop","Enqueue","Dequeue","Set","TryUpdate",
        "Sort","Reverse","Shuffle"
    };

    private int _seq;
    private string NextId(string prefix) => $"{prefix}#{_seq++}";

    public MutationWalker(SemanticModel sem, FlowGraph flow)
    {
        _sem = sem;
        _flow = flow;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Public entry
    // ═══════════════════════════════════════════════════════════════════════════

    public MutationGraph Walk()
    {
        BuildSymbolTable();
        BuildAliases();

        var root = _sem.SyntaxTree.GetRoot();
        foreach (var unit in _flow.Units)
        {
            var typeDecl = root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Identifier.Text == unit.Name);
            if (typeDecl == null) continue;

            foreach (var method in unit.Methods.Concat(unit.Constructors))
                WalkMethodBody(typeDecl, unit, method);
        }

        BuildStateChangeSummaries();
        return _mg;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 1 — Symbol table
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildSymbolTable()
    {
        foreach (var unit in _flow.Units)
        {
            foreach (var f in unit.Fields)
            {
                var sym = MakeSym(f.Id, f.Name, f.Type, "field", isShared: true, isReadonly: f.IsReadonly);
                _memberIndex[(unit.Name, f.Name)] = f.Id;
                Register(sym);
            }
            foreach (var p in unit.Properties)
            {
                var sym = MakeSym(p.Id, p.Name, p.Type, "property", isShared: true);
                _memberIndex[(unit.Name, p.Name)] = p.Id;
                Register(sym);
            }
            foreach (var method in unit.Methods.Concat(unit.Constructors))
            {
                foreach (var p in method.Params)
                {
                    var sym = MakeSym(p.Id, p.Name, p.Type, "param",
                        isShared: p.Modifier is "ref" or "out",
                        declaredIn: method.Id);
                    Register(sym);
                }
                foreach (var l in method.Locals)
                {
                    var sym = MakeSym(l.Id, l.Name, l.Type, "local",
                        isShared: false, declaredIn: method.Id);
                    Register(sym);
                }
            }
        }
    }

    private SymbolInfo MakeSym(string id, string name, string type, string kind,
        bool isShared = false, bool isReadonly = false, string? declaredIn = null)
    {
        string typeKind = ClassifyTypeKind(type);
        return new SymbolInfo
        {
            Id = id, Name = name, TypeName = type,
            TypeKind = typeKind, SymbolKind = kind,
            IsShared = isShared,
            IsReadonly = isReadonly,
            IsNullable = type.EndsWith("?") || typeKind == "reference" || typeKind == "interface",
            DeclaredInMethod = declaredIn
        };
    }

    private void Register(SymbolInfo sym)
    {
        _symById[sym.Id] = sym;
        _mg.Symbols.Add(sym);
    }

    private string ClassifyTypeKind(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName) || typeName == "var") return "unknown";
        string t = typeName.TrimEnd('?');

        if (ValueTypeNames.Contains(t)) return "value";
        if (t.StartsWith("List<") || t.StartsWith("IList<") ||
            t.StartsWith("IEnumerable<") || t.StartsWith("ICollection<") ||
            t.StartsWith("Dictionary<") || t.StartsWith("IDictionary<") ||
            t.StartsWith("HashSet<") || t.StartsWith("Queue<") ||
            t.StartsWith("Stack<") || t.EndsWith("[]"))
            return "reference";
        if (t.StartsWith("I") && t.Length > 1 && char.IsUpper(t[1]))
            return "interface";
        if (t == "string" || t == "String") return "reference"; // immutable but reference
        if (t == "object" || t == "dynamic") return "reference";
        // Heuristic: ends in known suffixes → likely a class
        if (t.EndsWith("Service") || t.EndsWith("Repository") || t.EndsWith("Manager") ||
            t.EndsWith("Handler") || t.EndsWith("Client") || t.EndsWith("Context") ||
            t.EndsWith("Request") || t.EndsWith("Result") || t.EndsWith("Response") ||
            t.EndsWith("Options") || t.EndsWith("Settings") || t.EndsWith("Model") ||
            t.EndsWith("Entity") || t.EndsWith("Dto") || t.EndsWith("DTO"))
            return "reference";
        // Task<T> / Task → reference but effectively value from mutation standpoint
        if (t.StartsWith("Task")) return "reference";
        return "unknown";
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 2 — Alias detection
    // Alias: local B is assigned from symbol A (both reference type) → same heap object
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildAliases()
    {
        foreach (var unit in _flow.Units)
        {
            foreach (var method in unit.Methods.Concat(unit.Constructors))
            {
                foreach (var local in method.Locals)
                {
                    if (!_symById.TryGetValue(local.Id, out var localSym)) continue;
                    if (localSym.TypeKind is not ("reference" or "interface" or "unknown")) continue;

                    foreach (var srcId in local.DataSourceIds)
                    {
                        if (!_symById.TryGetValue(srcId, out var srcSym)) continue;
                        if (srcSym.TypeKind is not ("reference" or "interface" or "unknown")) continue;

                        // They may point to the same object
                        if (!srcSym.AliasIds.Contains(local.Id))  srcSym.AliasIds.Add(local.Id);
                        if (!localSym.AliasIds.Contains(srcId))   localSym.AliasIds.Add(srcId);
                    }
                }
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // 3 — Walk method bodies to detect mutations
    // ═══════════════════════════════════════════════════════════════════════════

    private void WalkMethodBody(TypeDeclarationSyntax typeDecl, ClassUnit unit, MethodNode method)
    {
        SyntaxNode? body = null;
        if (method.Name == ".ctor")
            body = typeDecl.Members.OfType<ConstructorDeclarationSyntax>().FirstOrDefault();
        else
            body = typeDecl.Members
                .OfType<MethodDeclarationSyntax>()
                .FirstOrDefault(m => m.Identifier.Text == method.Name);

        if (body == null) return;

        var ctx = new WalkCtx(unit, method, null, false);

        if (body is MethodDeclarationSyntax mds)
        {
            if (mds.Body != null) WalkBlock(mds.Body, ctx);
            else if (mds.ExpressionBody != null) WalkExpr(mds.ExpressionBody.Expression, ctx);
        }
        else if (body is ConstructorDeclarationSyntax cds && cds.Body != null)
        {
            WalkBlock(cds.Body, ctx);
        }
    }

    private void WalkBlock(BlockSyntax block, WalkCtx ctx)
    {
        foreach (var s in block.Statements)
            WalkStmt(s, ctx);
    }

    private void WalkStmt(StatementSyntax stmt, WalkCtx ctx)
    {
        switch (stmt)
        {
            // ── expression statement: assignments, standalone calls ────────────
            case ExpressionStatementSyntax exprStmt:
                WalkExpr(exprStmt.Expression, ctx);
                break;

            // ── local var declaration: look for object-init or call result ────
            case LocalDeclarationStatementSyntax localDecl:
                foreach (var v in localDecl.Declaration.Variables)
                    if (v.Initializer != null)
                        WalkExpr(v.Initializer.Value, ctx);
                break;

            // ── if — fork guard for each branch ──────────────────────────────
            case IfStatementSyntax ifStmt:
            {
                string cond = ifStmt.Condition.ToString();
                bool isNull = cond.Contains("null");
                var symIds = SymbolIdsInExpr(ifStmt.Condition);

                WalkStmt(ifStmt.Statement, ctx.WithGuard(new ConditionGuard
                {
                    Expression = cond, Branch = "then", ConditionKind = isNull ? "null-check" : "if",
                    IsNullCheck = isNull, IsNegated = false, InvolvedSymbolIds = symIds
                }));
                if (ifStmt.Else?.Statement != null)
                    WalkStmt(ifStmt.Else.Statement, ctx.WithGuard(new ConditionGuard
                    {
                        Expression = $"!({cond})", Branch = "else", ConditionKind = isNull ? "null-check" : "if",
                        IsNullCheck = isNull, IsNegated = true, InvolvedSymbolIds = symIds
                    }));
                break;
            }

            // ── switch statement ──────────────────────────────────────────────
            case SwitchStatementSyntax sw:
            {
                string swExpr = sw.Expression.ToString();
                var symIds = SymbolIdsInExpr(sw.Expression);
                foreach (var section in sw.Sections)
                {
                    string label = string.Join("|", section.Labels.Select(l => l.ToString().Trim()));
                    var guard = new ConditionGuard
                    {
                        Expression = $"{swExpr} is {label}", Branch = "case",
                        ConditionKind = "switch-case", InvolvedSymbolIds = symIds
                    };
                    foreach (var s in section.Statements)
                        WalkStmt(s, ctx.WithGuard(guard));
                }
                break;
            }

            // ── loops — set inLoop flag ───────────────────────────────────────
            case ForEachStatementSyntax forEach:
            {
                string cond = $"foreach {forEach.Identifier} in {forEach.Expression}";
                var guard = new ConditionGuard
                {
                    Expression = cond, Branch = "loop-body", ConditionKind = "foreach",
                    InvolvedSymbolIds = SymbolIdsInExpr(forEach.Expression)
                };
                WalkStmt(forEach.Statement, ctx.WithGuard(guard).AsLoop());
                break;
            }
            case ForStatementSyntax forStmt:
            {
                string cond = forStmt.Condition?.ToString() ?? "";
                var guard = new ConditionGuard
                {
                    Expression = cond, Branch = "loop-body", ConditionKind = "for",
                    InvolvedSymbolIds = cond.Length > 0 ? SymbolIdsInExpr(forStmt.Condition!) : new()
                };
                WalkStmt(forStmt.Statement, ctx.WithGuard(guard).AsLoop());
                break;
            }
            case WhileStatementSyntax whileStmt:
            {
                string cond = whileStmt.Condition.ToString();
                var guard = new ConditionGuard
                {
                    Expression = cond, Branch = "loop-body", ConditionKind = "while",
                    InvolvedSymbolIds = SymbolIdsInExpr(whileStmt.Condition)
                };
                WalkStmt(whileStmt.Statement, ctx.WithGuard(guard).AsLoop());
                break;
            }

            // ── try/catch/finally ─────────────────────────────────────────────
            case TryStatementSyntax trySmt:
                WalkBlock(trySmt.Block, ctx);
                foreach (var c in trySmt.Catches)
                {
                    string catchExpr = c.Declaration?.Type.ToString() ?? "Exception";
                    var guard = new ConditionGuard
                    {
                        Expression = catchExpr, Branch = "catch", ConditionKind = "try-catch"
                    };
                    WalkBlock(c.Block, ctx.WithGuard(guard));
                }
                if (trySmt.Finally != null)
                {
                    var guard = new ConditionGuard
                    {
                        Expression = "always", Branch = "finally", ConditionKind = "try-finally"
                    };
                    WalkBlock(trySmt.Finally.Block, ctx.WithGuard(guard));
                }
                break;

            case BlockSyntax block:
                WalkBlock(block, ctx);
                break;

            case ReturnStatementSyntax ret:
                if (ret.Expression != null) WalkExpr(ret.Expression, ctx);
                break;
        }
    }

    private void WalkExpr(ExpressionSyntax expr, WalkCtx ctx)
    {
        // Unwrap await
        if (expr is AwaitExpressionSyntax aw) { WalkExpr(aw.Expression, ctx); return; }

        // ── Assignment: target = source ───────────────────────────────────────
        if (expr is AssignmentExpressionSyntax assign)
        {
            RecordAssignmentMutation(assign, ctx);
            WalkExpr(assign.Right, ctx); // recurse in case RHS has nested calls
            return;
        }

        // ── Invocation: receiver.Method(args) ────────────────────────────────
        if (expr is InvocationExpressionSyntax inv)
        {
            RecordInvocationMutation(inv, ctx);
            // Recurse into arguments in case they contain mutations
            foreach (var arg in inv.ArgumentList.Arguments)
                WalkExpr(arg.Expression, ctx);
            return;
        }

        // ── Conditional / ternary — fork guard ───────────────────────────────
        if (expr is ConditionalExpressionSyntax ternary)
        {
            string cond = ternary.Condition.ToString();
            bool isNull = cond.Contains("null");
            var ids = SymbolIdsInExpr(ternary.Condition);
            WalkExpr(ternary.WhenTrue,  ctx.WithGuard(new ConditionGuard { Expression = cond,       Branch = "then", ConditionKind = isNull ? "null-check" : "ternary", IsNullCheck = isNull, InvolvedSymbolIds = ids }));
            WalkExpr(ternary.WhenFalse, ctx.WithGuard(new ConditionGuard { Expression = $"!({cond})", Branch = "else", ConditionKind = isNull ? "null-check" : "ternary", IsNullCheck = isNull, IsNegated = true, InvolvedSymbolIds = ids }));
            return;
        }

        // ── Null coalescing — RHS is guarded by LHS == null ──────────────────
        if (expr is BinaryExpressionSyntax { OperatorToken: { Text: "??" } } nullCoal)
        {
            WalkExpr(nullCoal.Left, ctx);
            WalkExpr(nullCoal.Right, ctx.WithGuard(new ConditionGuard
            {
                Expression = $"{nullCoal.Left} == null", Branch = "else",
                ConditionKind = "null-check", IsNullCheck = true,
                InvolvedSymbolIds = SymbolIdsInExpr(nullCoal.Left)
            }));
            return;
        }

        // ── Object initializer: new Foo { Prop = val, ... } ──────────────────
        if (expr is ObjectCreationExpressionSyntax objCreate)
        {
            if (objCreate.Initializer != null)
                foreach (var init in objCreate.Initializer.Expressions)
                    WalkExpr(init, ctx);
            if (objCreate.ArgumentList != null)
                foreach (var arg in objCreate.ArgumentList.Arguments)
                    WalkExpr(arg.Expression, ctx);
            return;
        }
    }

    // ── Assignment mutation ───────────────────────────────────────────────────

    private void RecordAssignmentMutation(AssignmentExpressionSyntax assign, WalkCtx ctx)
    {
        var left = assign.Left;

        // Determine if this is a member mutation (obj.Prop = x  or  obj._field = x)
        // vs a plain local reassignment (local = x)
        if (left is MemberAccessExpressionSyntax memberAccess)
        {
            // obj.Member = value
            string receiverText = memberAccess.Expression.ToString();
            string memberName   = memberAccess.Name.Identifier.Text;
            string? targetSymId = ResolveSymbolId(receiverText, ctx);

            // Check if receiver is a known reference type
            bool isSharedReceiver = targetSymId != null && _symById.TryGetValue(targetSymId, out var ts) && ts.IsShared;

            EmitMutation(new MutationNode
            {
                Id = NextId("mut"),
                Kind = "property-set",
                TargetSymbolId = targetSymId ?? receiverText,
                TargetMember = memberName,
                SourceExpression = assign.Right.ToString(),
                SourceIds = SymbolIdsInExpr(assign.Right),
                MethodId = ctx.Method.Id,
                IsOnSharedObject = isSharedReceiver,
                IsInsideLoop = ctx.InLoop,
                Guard = ctx.Guard,
                AffectedAliasIds = AliasIdsFor(targetSymId)
            });
        }
        else if (left is ElementAccessExpressionSyntax elemAccess)
        {
            // arr[i] = value
            string receiverText = elemAccess.Expression.ToString();
            string? targetSymId = ResolveSymbolId(receiverText, ctx);
            bool isShared = targetSymId != null && _symById.TryGetValue(targetSymId, out var ts2) && ts2.IsShared;

            string indexExpr = string.Join(", ", elemAccess.ArgumentList.Arguments.Select(a => a.ToString()));

            EmitMutation(new MutationNode
            {
                Id = NextId("mut"),
                Kind = "index-write",
                TargetSymbolId = targetSymId ?? receiverText,
                TargetMember = $"[{indexExpr}]",
                SourceExpression = assign.Right.ToString(),
                SourceIds = SymbolIdsInExpr(assign.Right),
                MethodId = ctx.Method.Id,
                IsOnSharedObject = isShared,
                IsInsideLoop = ctx.InLoop,
                Guard = ctx.Guard,
                AffectedAliasIds = AliasIdsFor(targetSymId)
            });
        }
        else if (left is IdentifierNameSyntax ident)
        {
            // Plain identifier: could be a field on 'this' or a local reassignment
            string name = ident.Identifier.Text;
            string? symId = ResolveSymbolId(name, ctx);

            if (symId != null && _symById.TryGetValue(symId, out var sym))
            {
                // Only emit as mutation if it's a field/property (shared) or ref/out param
                if (sym.IsShared || sym.SymbolKind == "param" && (sym.TypeKind == "reference"))
                {
                    EmitMutation(new MutationNode
                    {
                        Id = NextId("mut"),
                        Kind = sym.SymbolKind == "field" ? "field-write" : "property-set",
                        TargetSymbolId = symId,
                        TargetMember = null, // whole-symbol write
                        SourceExpression = assign.Right.ToString(),
                        SourceIds = SymbolIdsInExpr(assign.Right),
                        MethodId = ctx.Method.Id,
                        IsOnSharedObject = sym.IsShared,
                        IsInsideLoop = ctx.InLoop,
                        Guard = ctx.Guard,
                        AffectedAliasIds = AliasIdsFor(symId)
                    });
                }
            }
        }
    }

    // ── Invocation mutation ───────────────────────────────────────────────────

    private void RecordInvocationMutation(InvocationExpressionSyntax inv, WalkCtx ctx)
    {
        if (inv.Expression is not MemberAccessExpressionSyntax ma) return;

        string methodName   = ma.Name.Identifier.Text;
        string receiverText = ma.Expression.ToString();
        string? targetSymId = ResolveSymbolId(receiverText, ctx);

        // ── Collection mutators ───────────────────────────────────────────────
        if (CollectionMutators.Contains(methodName) && targetSymId != null)
        {
            bool isShared = _symById.TryGetValue(targetSymId, out var ts) && ts.IsShared;
            string kind = methodName is "Add" or "AddRange" or "TryAdd" or "Insert" or "InsertRange"
                ? "collection-add"
                : methodName is "Remove" or "RemoveAt" or "RemoveAll" or "RemoveRange" or "RemoveWhere" or "TryRemove" or "Clear"
                ? "collection-remove"
                : methodName is "Sort" or "Reverse" or "Shuffle"
                ? "collection-sort"
                : "collection-add";

            EmitMutation(new MutationNode
            {
                Id = NextId("mut"),
                Kind = kind,
                TargetSymbolId = targetSymId,
                TargetMember = methodName,
                SourceExpression = inv.ToString(),
                SourceIds = inv.ArgumentList.Arguments.Select(a => ResolveSymbolId(a.Expression.ToString(), ctx)).Where(x => x != null).Select(x => x!).ToList(),
                MethodId = ctx.Method.Id,
                IsOnSharedObject = isShared,
                IsInsideLoop = ctx.InLoop,
                Guard = ctx.Guard,
                AffectedAliasIds = AliasIdsFor(targetSymId)
            });
            return;
        }

        // ── External call on a reference-type receiver ────────────────────────
        // These MAY mutate the receiver — we record them so the red-flag detector can decide
        if (targetSymId != null && _symById.TryGetValue(targetSymId, out var recvSym))
        {
            if (recvSym.TypeKind is "reference" or "interface" or "unknown")
            {
                // Only record if the receiver is a field/param (shared or passed in)
                if (recvSym.IsShared || recvSym.SymbolKind == "param")
                {
                    EmitMutation(new MutationNode
                    {
                        Id = NextId("mut"),
                        Kind = "external-call",
                        TargetSymbolId = targetSymId,
                        TargetMember = methodName,
                        SourceExpression = inv.ToString(),
                        SourceIds = inv.ArgumentList.Arguments.Select(a => ResolveSymbolId(a.Expression.ToString(), ctx)).Where(x => x != null).Select(x => x!).ToList(),
                        MethodId = ctx.Method.Id,
                        IsOnSharedObject = recvSym.IsShared,
                        IsInsideLoop = ctx.InLoop,
                        Guard = ctx.Guard,
                        AffectedAliasIds = AliasIdsFor(targetSymId)
                    });
                }
            }
        }

        // ── ref/out arguments ─────────────────────────────────────────────────
        foreach (var arg in inv.ArgumentList.Arguments)
        {
            if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword) || arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))
            {
                string? argSymId = ResolveSymbolId(arg.Expression.ToString(), ctx);
                if (argSymId != null)
                {
                    EmitMutation(new MutationNode
                    {
                        Id = NextId("mut"),
                        Kind = "ref-write",
                        TargetSymbolId = argSymId,
                        TargetMember = null,
                        SourceExpression = inv.ToString(),
                        SourceIds = new() { argSymId },
                        MethodId = ctx.Method.Id,
                        IsOnSharedObject = _symById.TryGetValue(argSymId, out var argSym) && argSym.IsShared,
                        IsInsideLoop = ctx.InLoop,
                        Guard = ctx.Guard,
                        AffectedAliasIds = AliasIdsFor(argSymId)
                    });
                }
            }
        }
    }

    private void EmitMutation(MutationNode m) => _mg.Mutations.Add(m);

    // ═══════════════════════════════════════════════════════════════════════════
    // 4 — State change summaries
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildStateChangeSummaries()
    {
        // Group mutations by targetSymbolId
        var byTarget = _mg.Mutations
            .GroupBy(m => m.TargetSymbolId)
            .ToDictionary(g => g.Key, g => g.ToList());

        // Also find which methods READ each symbol (appears in any source edge)
        var readByMethod = new Dictionary<string, HashSet<string>>();
        foreach (var unit in _flow.Units)
            foreach (var method in unit.Methods.Concat(unit.Constructors))
                foreach (var edge in method.FlowEdges)
                {
                    readByMethod.TryAdd(edge.From, new());
                    readByMethod[edge.From].Add(method.Id);
                }

        foreach (var (symId, mutations) in byTarget)
        {
            if (!_symById.TryGetValue(symId, out var sym)) continue;

            var writtenFrom = mutations.Select(m => m.MethodId).Distinct().ToList();
            readByMethod.TryGetValue(symId, out var reads);

            var hasAliasedExternal = mutations
                .Where(m => m.Kind == "external-call")
                .Any(m => m.AffectedAliasIds.Count > 0);

            _mg.StateChangeSummaries.Add(new StateChangeSummary
            {
                SymbolId = symId,
                SymbolName = sym.Name,
                TypeName = sym.TypeName,
                TypeKind = sym.TypeKind,
                IsShared = sym.IsShared,
                Mutations = mutations.Select(m => new MutationRef
                {
                    MutationId = m.Id,
                    MethodId = m.MethodId,
                    Kind = m.Kind,
                    Member = m.TargetMember,
                    GuardExpression = m.Guard?.Expression,
                    GuardBranch = m.Guard?.Branch,
                    IsInsideLoop = m.IsInsideLoop
                }).ToList(),
                WrittenFromMethods = writtenFrom,
                ReadFromMethods = reads?.ToList() ?? new(),
                HasConditionalMutation = mutations.Any(m => m.Guard != null),
                IsWrittenFromMultipleMethods = writtenFrom.Count > 1,
                HasMutationInLoop = mutations.Any(m => m.IsInsideLoop),
                HasAliasedExternalCall = hasAliasedExternal
            });
        }

        // Sort: shared first, then by mutation count desc
        _mg.StateChangeSummaries.Sort((a, b) =>
        {
            if (a.IsShared != b.IsShared) return a.IsShared ? -1 : 1;
            return b.Mutations.Count.CompareTo(a.Mutations.Count);
        });
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private string? ResolveSymbolId(string name, WalkCtx ctx)
    {
        // Try direct match in param/local ids via name
        foreach (var p in ctx.Method.Params)
            if (p.Name == name) return p.Id;
        foreach (var l in ctx.Method.Locals)
            if (l.Name == name) return l.Id;

        // Try field/property on the current class
        if (_memberIndex.TryGetValue((ctx.Unit.Name, name), out var memberId)) return memberId;

        // _fieldName pattern (private fields often prefixed with _)
        string stripped = name.TrimStart('_');
        if (_memberIndex.TryGetValue((ctx.Unit.Name, stripped), out var strippedId)) return strippedId;
        if (stripped.Length > 0)
        {
            string camel = char.ToLower(stripped[0]) + stripped[1..];
            if (_memberIndex.TryGetValue((ctx.Unit.Name, camel), out var camelId)) return camelId;
        }

        // Check all registered symbols by name as fallback
        return _symById.Values.FirstOrDefault(s => s.Name == name)?.Id;
    }

    private List<string> SymbolIdsInExpr(ExpressionSyntax expr)
    {
        // No ctx here — just return all identifiers that match registered symbols
        return expr.DescendantNodesAndSelf()
            .OfType<IdentifierNameSyntax>()
            .Select(id => _symById.Values.FirstOrDefault(s => s.Name == id.Identifier.Text)?.Id)
            .Where(id => id != null)
            .Select(id => id!)
            .Distinct()
            .ToList();
    }

    private List<string> AliasIdsFor(string? symId)
    {
        if (symId == null) return new();
        return _symById.TryGetValue(symId, out var sym) ? sym.AliasIds : new();
    }

    // ── Walk context — immutable record of current scope ─────────────────────

    private record WalkCtx(ClassUnit Unit, MethodNode Method, ConditionGuard? Guard, bool InLoop)
    {
        public WalkCtx WithGuard(ConditionGuard g) => this with { Guard = g };
        public WalkCtx AsLoop() => this with { InLoop = true };
    }
}
