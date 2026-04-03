using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Walks a Roslyn SyntaxTree + SemanticModel and builds the FlowGraph.
/// Covers:
///   - Method-level: params, locals, assignments, calls, returns
///   - Inter-method: call → resolved method, argument → param edges
///   - Inter-class: field reads/writes, DI via constructors
/// </summary>
public class DataFlowWalker
{
    private readonly SemanticModel _model;
    private readonly FlowGraph _graph;

    // Index of all nodes by id for cross-linking
    private readonly Dictionary<string, FieldNode> _fieldIndex = new();
    private readonly Dictionary<string, PropertyNode> _propIndex = new();
    private readonly Dictionary<string, MethodNode> _methodIndex = new();
    private readonly Dictionary<string, ParamNode> _paramIndex = new();
    private readonly Dictionary<string, LocalNode> _localIndex = new();

    // Per-method symbol table: name → nodeId (for resolving references)
    private readonly Dictionary<string, string> _scope = new();
    private string _currentClassId = "";
    private string _currentMethodId = "";

    public DataFlowWalker(SemanticModel model, string sourceFile)
    {
        _model = model;
        _graph = new FlowGraph { Source = sourceFile };
    }

    public FlowGraph Walk()
    {
        var root = _model.SyntaxTree.GetRoot();

        foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            ProcessType(typeDecl);

        LinkInterMethodEdges();
        return _graph;
    }

    // ── Type ─────────────────────────────────────────────────────────────────

    private void ProcessType(TypeDeclarationSyntax typeDecl)
    {
        var symbol = _model.GetDeclaredSymbol(typeDecl);
        string ns = symbol?.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>") ns = "";
        string className = typeDecl.Identifier.Text;
        string classId = IdGen.Class(ns, className);
        _currentClassId = classId;

        var unit = new ClassUnit
        {
            Id = classId,
            Name = className,
            Namespace = ns.Length > 0 ? ns : null,
            Kind = typeDecl switch
            {
                InterfaceDeclarationSyntax => "interface",
                StructDeclarationSyntax => "struct",
                RecordDeclarationSyntax => "record",
                _ => "class"
            }
        };

        // Base types
        if (typeDecl.BaseList != null)
            unit.BaseTypes = typeDecl.BaseList.Types.Select(t => t.ToString()).ToList();

        // Fields
        foreach (var fieldDecl in typeDecl.Members.OfType<FieldDeclarationSyntax>())
            ProcessFields(fieldDecl, classId, unit);

        // Properties
        foreach (var propDecl in typeDecl.Members.OfType<PropertyDeclarationSyntax>())
            ProcessProperty(propDecl, classId, unit);

        // Constructors
        int ctorIndex = 0;
        foreach (var ctor in typeDecl.Members.OfType<ConstructorDeclarationSyntax>())
            unit.Constructors.Add(ProcessConstructor(ctor, classId, ctorIndex++));

        // Methods
        var methodCounts = new Dictionary<string, int>();
        foreach (var method in typeDecl.Members.OfType<MethodDeclarationSyntax>())
        {
            string methodName = method.Identifier.Text;
            methodCounts.TryGetValue(methodName, out int idx);
            unit.Methods.Add(ProcessMethod(method, classId, methodName, idx));
            methodCounts[methodName] = idx + 1;
        }

        _graph.Units.Add(unit);
    }

    // ── Fields ───────────────────────────────────────────────────────────────

    private void ProcessFields(FieldDeclarationSyntax fieldDecl, string classId, ClassUnit unit)
    {
        string accessibility = GetAccessibility(fieldDecl.Modifiers);
        bool isReadonly = fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.ReadOnlyKeyword));
        bool isStatic = fieldDecl.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword));
        string typeName = fieldDecl.Declaration.Type.ToString();

        foreach (var variable in fieldDecl.Declaration.Variables)
        {
            string name = variable.Identifier.Text;
            string id = IdGen.Field(classId, name);
            var node = new FieldNode
            {
                Id = id,
                Name = name,
                Type = typeName,
                Accessibility = accessibility,
                IsReadonly = isReadonly,
                IsStatic = isStatic,
                Initializer = variable.Initializer?.Value?.ToString()
            };
            unit.Fields.Add(node);
            _fieldIndex[id] = node;
            // Register in a class-level scope so methods can find it
            _fieldIndex[$"{classId}::{name}"] = node;
        }
    }

    // ── Properties ───────────────────────────────────────────────────────────

    private void ProcessProperty(PropertyDeclarationSyntax propDecl, string classId, ClassUnit unit)
    {
        string name = propDecl.Identifier.Text;
        string id = IdGen.Property(classId, name);
        bool isAuto = propDecl.AccessorList?.Accessors.All(a => a.Body == null && a.ExpressionBody == null) ?? false;

        var node = new PropertyNode
        {
            Id = id,
            Name = name,
            Type = propDecl.Type.ToString(),
            Accessibility = GetAccessibility(propDecl.Modifiers),
            HasGetter = propDecl.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.GetAccessorDeclaration)) ?? propDecl.ExpressionBody != null,
            HasSetter = propDecl.AccessorList?.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) ?? false,
            IsAutoProperty = isAuto
        };
        unit.Properties.Add(node);
        _propIndex[id] = node;
        _propIndex[$"{classId}::{name}"] = node;
    }

    // ── Constructor ──────────────────────────────────────────────────────────

    private MethodNode ProcessConstructor(ConstructorDeclarationSyntax ctor, string classId, int index)
    {
        string methodId = IdGen.Constructor(classId, index);
        _currentMethodId = methodId;
        _scope.Clear();

        var node = new MethodNode
        {
            Id = methodId,
            Name = ".ctor",
            ReturnType = "void",
            Accessibility = GetAccessibility(ctor.Modifiers),
            IsAsync = false,
            IsStatic = ctor.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
        };

        ProcessParams(ctor.ParameterList, methodId, node);

        if (ctor.Body != null)
            ProcessBody(ctor.Body, methodId, node);
        else if (ctor.ExpressionBody != null)
            ProcessExpressionBody(ctor.ExpressionBody, methodId, node);

        _methodIndex[methodId] = node;
        return node;
    }

    // ── Method ───────────────────────────────────────────────────────────────

    private MethodNode ProcessMethod(MethodDeclarationSyntax method, string classId, string name, int overloadIndex)
    {
        string methodId = IdGen.Method(classId, name, overloadIndex);
        _currentMethodId = methodId;
        _scope.Clear();

        // Re-seed scope with class fields/props so they're resolvable in the body
        foreach (var kv in _fieldIndex)
            if (kv.Key.StartsWith($"{classId}::") && !kv.Key.Contains("/"))
            {
                string fname = kv.Key.Substring(classId.Length + 2).TrimStart('f','i','e','l','d',':');
                _scope[$"_{fname}"] = kv.Value.Id; // _fieldName pattern
                _scope[kv.Value.Name] = kv.Value.Id;
            }

        var node = new MethodNode
        {
            Id = methodId,
            Name = name,
            ReturnType = method.ReturnType.ToString(),
            Accessibility = GetAccessibility(method.Modifiers),
            IsAsync = method.Modifiers.Any(m => m.IsKind(SyntaxKind.AsyncKeyword)),
            IsStatic = method.Modifiers.Any(m => m.IsKind(SyntaxKind.StaticKeyword))
        };

        ProcessParams(method.ParameterList, methodId, node);

        if (method.Body != null)
            ProcessBody(method.Body, methodId, node);
        else if (method.ExpressionBody != null)
            ProcessExpressionBody(method.ExpressionBody, methodId, node);

        _methodIndex[methodId] = node;
        return node;
    }

    // ── Parameters ───────────────────────────────────────────────────────────

    private void ProcessParams(ParameterListSyntax paramList, string methodId, MethodNode node)
    {
        foreach (var p in paramList.Parameters)
        {
            string name = p.Identifier.Text;
            string id = IdGen.Param(methodId, name);
            string? modifier = null;
            if (p.Modifiers.Any(m => m.IsKind(SyntaxKind.RefKeyword)))       modifier = "ref";
            else if (p.Modifiers.Any(m => m.IsKind(SyntaxKind.OutKeyword)))  modifier = "out";
            else if (p.Modifiers.Any(m => m.IsKind(SyntaxKind.InKeyword)))   modifier = "in";

            var param = new ParamNode
            {
                Id = id,
                Name = name,
                Type = p.Type?.ToString() ?? "object",
                HasDefault = p.Default != null,
                DefaultValue = p.Default?.Value?.ToString(),
                Modifier = modifier
            };
            node.Params.Add(param);
            _paramIndex[id] = param;
            _scope[name] = id;
        }
    }

    // ── Body ─────────────────────────────────────────────────────────────────

    private void ProcessBody(BlockSyntax body, string methodId, MethodNode node)
    {
        var localCounts = new Dictionary<string, int>();
        var assignCounts = new Dictionary<string, int>();
        var callCounts = new Dictionary<string, int>();
        int returnCount = 0;

        foreach (var stmt in body.Statements)
            ProcessStatement(stmt, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
    }

    private void ProcessExpressionBody(ArrowExpressionClauseSyntax exprBody, string methodId, MethodNode node)
    {
        var callCounts = new Dictionary<string, int>();
        int returnCount = 0;
        var returnId = IdGen.Return(methodId, returnCount++);
        var expr = exprBody.Expression;

        var ret = new ReturnNode
        {
            Id = returnId,
            Expression = expr.ToString(),
            SourceIds = ResolveExpressionSources(expr)
        };
        node.Returns.Add(ret);

        foreach (var srcId in ret.SourceIds)
            node.FlowEdges.Add(new FlowEdge { From = srcId, To = returnId, Kind = "return" });

        // Also extract any calls within the expression
        ExtractCallsFromExpression(expr, methodId, node, callCounts);
    }

    private void ProcessStatement(StatementSyntax stmt, string methodId, MethodNode node,
        Dictionary<string, int> localCounts, Dictionary<string, int> assignCounts,
        Dictionary<string, int> callCounts, ref int returnCount)
    {
        switch (stmt)
        {
            case LocalDeclarationStatementSyntax localDecl:
                ProcessLocalDeclaration(localDecl, methodId, node, localCounts);
                break;

            case ExpressionStatementSyntax exprStmt:
                ProcessExpressionStatement(exprStmt, methodId, node, assignCounts, callCounts);
                break;

            case ReturnStatementSyntax retStmt:
                ProcessReturn(retStmt, methodId, node, ref returnCount);
                break;

            case IfStatementSyntax ifStmt:
                // Walk branches
                ProcessStatement(ifStmt.Statement, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                if (ifStmt.Else?.Statement != null)
                    ProcessStatement(ifStmt.Else.Statement, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                break;

            case BlockSyntax block:
                foreach (var s in block.Statements)
                    ProcessStatement(s, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                break;

            case ForEachStatementSyntax forEach:
                // The iteration variable is a local
                {
                    string varName = forEach.Identifier.Text;
                    localCounts.TryGetValue(varName, out int lIdx);
                    string localId = IdGen.Local(methodId, varName, lIdx);
                    localCounts[varName] = lIdx + 1;
                    var local = new LocalNode
                    {
                        Id = localId,
                        Name = varName,
                        Type = forEach.Type.ToString(),
                        IsVar = forEach.Type.IsVar,
                        InitExpression = $"foreach element of {forEach.Expression}",
                        DataSourceIds = ResolveExpressionSources(forEach.Expression)
                    };
                    node.Locals.Add(local);
                    _localIndex[localId] = local;
                    _scope[varName] = localId;
                    foreach (var src in local.DataSourceIds)
                        node.FlowEdges.Add(new FlowEdge { From = src, To = localId, Kind = "assignment", Label = "foreach" });
                    ProcessStatement(forEach.Statement, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                }
                break;

            case ForStatementSyntax forStmt:
                if (forStmt.Declaration != null)
                {
                    var fakeLocal = SyntaxFactory.LocalDeclarationStatement(forStmt.Declaration);
                    ProcessLocalDeclaration(fakeLocal, methodId, node, localCounts);
                }
                if (forStmt.Statement != null)
                    ProcessStatement(forStmt.Statement, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                break;

            case WhileStatementSyntax whileStmt:
                ProcessStatement(whileStmt.Statement, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                break;

            case TryStatementSyntax tryStmt:
                ProcessStatement(tryStmt.Block, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                foreach (var catchClause in tryStmt.Catches)
                {
                    if (catchClause.Declaration != null)
                    {
                        string exName = catchClause.Declaration.Identifier.Text;
                        if (!string.IsNullOrEmpty(exName))
                        {
                            localCounts.TryGetValue(exName, out int lIdx);
                            string exId = IdGen.Local(methodId, exName, lIdx);
                            var exLocal = new LocalNode
                            {
                                Id = exId, Name = exName,
                                Type = catchClause.Declaration.Type.ToString(),
                                InitExpression = "caught exception"
                            };
                            node.Locals.Add(exLocal);
                            _scope[exName] = exId;
                        }
                    }
                    ProcessStatement(catchClause.Block, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                }
                if (tryStmt.Finally != null)
                    ProcessStatement(tryStmt.Finally.Block, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                break;

            // Anything else: still descend to find nested returns/calls
            default:
                foreach (var nested in stmt.ChildNodes().OfType<StatementSyntax>())
                    ProcessStatement(nested, methodId, node, localCounts, assignCounts, callCounts, ref returnCount);
                break;
        }
    }

    // ── Local variable declaration ────────────────────────────────────────────

    private void ProcessLocalDeclaration(LocalDeclarationStatementSyntax localDecl, string methodId,
        MethodNode node, Dictionary<string, int> localCounts)
    {
        string typeName = localDecl.Declaration.Type.ToString();

        foreach (var variable in localDecl.Declaration.Variables)
        {
            string name = variable.Identifier.Text;
            localCounts.TryGetValue(name, out int idx);
            string localId = IdGen.Local(methodId, name, idx);
            localCounts[name] = idx + 1;

            var local = new LocalNode
            {
                Id = localId,
                Name = name,
                Type = typeName,
                IsVar = localDecl.Declaration.Type.IsVar,
                InitExpression = variable.Initializer?.Value?.ToString()
            };

            if (variable.Initializer?.Value != null)
            {
                local.DataSourceIds = ResolveExpressionSources(variable.Initializer.Value);
                foreach (var srcId in local.DataSourceIds)
                    node.FlowEdges.Add(new FlowEdge { From = srcId, To = localId, Kind = "assignment" });

                // If RHS contains a method call, extract it too
                ExtractCallsFromExpression(variable.Initializer.Value, methodId, node,
                    new Dictionary<string, int>(), resultAssignedTo: name, resultAssignedToId: localId);
            }

            node.Locals.Add(local);
            _localIndex[localId] = local;
            _scope[name] = localId;
        }
    }

    // ── Expression statements (assignments, standalone calls) ─────────────────

    private void ProcessExpressionStatement(ExpressionStatementSyntax exprStmt, string methodId,
        MethodNode node, Dictionary<string, int> assignCounts, Dictionary<string, int> callCounts)
    {
        var expr = exprStmt.Expression;

        if (expr is AssignmentExpressionSyntax assign)
        {
            string target = assign.Left.ToString();
            assignCounts.TryGetValue(target, out int idx);
            string assignId = IdGen.Assignment(methodId, target, idx);
            assignCounts[target] = idx + 1;

            string? targetId = ResolveSymbol(assign.Left);
            var sources = ResolveExpressionSources(assign.Right);

            var aNode = new AssignmentNode
            {
                Id = assignId,
                Target = target,
                TargetId = targetId,
                Expression = assign.Right.ToString(),
                Operator = assign.OperatorToken.Text,
                SourceIds = sources
            };
            node.Assignments.Add(aNode);

            foreach (var srcId in sources)
                node.FlowEdges.Add(new FlowEdge { From = srcId, To = targetId ?? assignId, Kind = "assignment" });
            if (targetId != null)
                node.FlowEdges.Add(new FlowEdge { From = assignId, To = targetId, Kind = "field-write", Label = target });

            ExtractCallsFromExpression(assign.Right, methodId, node, callCounts, target, targetId);
        }
        else
        {
            // Standalone call (e.g. await _repo.SaveAsync(x))
            ExtractCallsFromExpression(expr, methodId, node, callCounts);
        }
    }

    // ── Return statements ─────────────────────────────────────────────────────

    private void ProcessReturn(ReturnStatementSyntax retStmt, string methodId, MethodNode node, ref int returnCount)
    {
        string returnId = IdGen.Return(methodId, returnCount++);
        var ret = new ReturnNode
        {
            Id = returnId,
            Expression = retStmt.Expression?.ToString()
        };

        if (retStmt.Expression != null)
        {
            ret.SourceIds = ResolveExpressionSources(retStmt.Expression);
            foreach (var srcId in ret.SourceIds)
                node.FlowEdges.Add(new FlowEdge { From = srcId, To = returnId, Kind = "return" });
            ExtractCallsFromExpression(retStmt.Expression, methodId, node, new Dictionary<string, int>());
        }

        node.Returns.Add(ret);
    }

    // ── Call extraction ───────────────────────────────────────────────────────

    private void ExtractCallsFromExpression(ExpressionSyntax expr, string methodId, MethodNode node,
        Dictionary<string, int> callCounts, string? resultAssignedTo = null, string? resultAssignedToId = null)
    {
        // Unwrap await
        if (expr is AwaitExpressionSyntax awaitExpr)
        {
            ExtractCallsFromExpression(awaitExpr.Expression, methodId, node, callCounts, resultAssignedTo, resultAssignedToId);
            return;
        }

        // Handle object initializer: new Foo { A = x, B = y }
        if (expr is ObjectCreationExpressionSyntax objCreate)
        {
            if (objCreate.ArgumentList != null)
                foreach (var arg in objCreate.ArgumentList.Arguments)
                    ExtractCallsFromExpression(arg.Expression, methodId, node, callCounts);
            if (objCreate.Initializer != null)
                foreach (var initExpr in objCreate.Initializer.Expressions)
                    if (initExpr is AssignmentExpressionSyntax initAssign)
                        ExtractCallsFromExpression(initAssign.Right, methodId, node, callCounts);
            return;
        }

        // Collect all invocation expressions (handles chained calls too)
        var invocations = expr.DescendantNodesAndSelf().OfType<InvocationExpressionSyntax>().ToList();
        if (invocations.Count == 0) return;

        // For chained calls: outermost gets the result assignment
        for (int i = 0; i < invocations.Count; i++)
        {
            var inv = invocations[i];
            bool isOutermost = i == 0;

            string callText = inv.ToString();
            callCounts.TryGetValue(callText, out int callIdx);
            string callId = IdGen.Call(methodId, callText, callIdx);
            callCounts[callText] = callIdx + 1;

            // Determine receiver
            string? receiver = null;
            string? receiverKind = null;
            string methodName = callText;
            bool isChained = invocations.Count > 1 && !isOutermost;

            if (inv.Expression is MemberAccessExpressionSyntax memberAccess)
            {
                receiver = memberAccess.Expression.ToString();
                methodName = memberAccess.Name.Identifier.Text;
                receiverKind = ClassifyReceiver(memberAccess.Expression);
            }
            else if (inv.Expression is IdentifierNameSyntax ident)
            {
                methodName = ident.Identifier.Text;
                receiver = null;
                receiverKind = "local";
            }

            bool isAwaited = expr is AwaitExpressionSyntax ||
                             inv.Parent is AwaitExpressionSyntax ||
                             inv.Ancestors().Any(a => a is AwaitExpressionSyntax);

            var callNode = new CallNode
            {
                Id = callId,
                Expression = callText.Length > 80 ? callText[..80] + "…" : callText,
                MethodName = methodName,
                Receiver = receiver,
                ReceiverKind = receiverKind,
                IsAwaited = isAwaited,
                IsChained = isChained,
                ResultAssignedTo = isOutermost ? resultAssignedTo : null,
                ResultAssignedToId = isOutermost ? resultAssignedToId : null
            };

            // Arguments
            int argIdx = 0;
            foreach (var arg in inv.ArgumentList.Arguments)
            {
                string? argMod = null;
                if (arg.RefKindKeyword.IsKind(SyntaxKind.RefKeyword))  argMod = "ref";
                if (arg.RefKindKeyword.IsKind(SyntaxKind.OutKeyword))  argMod = "out";
                if (arg.RefKindKeyword.IsKind(SyntaxKind.InKeyword))   argMod = "in";

                var argNode = new ArgumentNode
                {
                    Index = argIdx++,
                    Name = arg.NameColon?.Name.Identifier.Text,
                    Expression = arg.Expression.ToString(),
                    SourceId = ResolveSymbol(arg.Expression),
                    Modifier = argMod
                };
                callNode.Arguments.Add(argNode);

                // Flow edge: source → callId (argument)
                if (argNode.SourceId != null)
                    node.FlowEdges.Add(new FlowEdge
                    {
                        From = argNode.SourceId, To = callId, Kind = "argument",
                        Label = $"arg[{argNode.Index}]"
                    });
            }

            // If result goes to a local, add that edge
            if (resultAssignedToId != null && isOutermost)
                node.FlowEdges.Add(new FlowEdge { From = callId, To = resultAssignedToId, Kind = "inter-method", Label = "return value" });

            node.Calls.Add(callNode);
        }
    }

    // ── Expression source resolution ──────────────────────────────────────────

    /// <summary>
    /// Collects node IDs of all identifiers referenced in an expression.
    /// </summary>
    private List<string> ResolveExpressionSources(ExpressionSyntax expr)
    {
        var ids = new HashSet<string>();

        foreach (var ident in expr.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>())
        {
            var id = ResolveSymbol(ident);
            if (id != null) ids.Add(id);
        }

        // Also check member access (e.g. request.CustomerId → param:request)
        foreach (var memberAccess in expr.DescendantNodesAndSelf().OfType<MemberAccessExpressionSyntax>())
        {
            var id = ResolveSymbol(memberAccess.Expression);
            if (id != null) ids.Add(id);
        }

        return ids.ToList();
    }

    /// <summary>
    /// Tries to resolve an expression to a known node ID in the current scope.
    /// </summary>
    private string? ResolveSymbol(ExpressionSyntax expr)
    {
        string text = expr switch
        {
            IdentifierNameSyntax ident => ident.Identifier.Text,
            MemberAccessExpressionSyntax ma => ma.Expression.ToString(),
            _ => expr.ToString()
        };

        if (_scope.TryGetValue(text, out var id)) return id;

        // Try field lookup with class prefix
        string fieldKey = $"{_currentClassId}::{text}";
        if (_fieldIndex.TryGetValue(fieldKey, out var f)) return f.Id;
        if (_propIndex.TryGetValue(fieldKey, out var p)) return p.Id;

        return null;
    }

    private string? ClassifyReceiver(ExpressionSyntax receiverExpr)
    {
        string text = receiverExpr.ToString();
        if (text == "this") return "this";

        if (_scope.TryGetValue(text, out var id))
        {
            if (_fieldIndex.ContainsKey(id)) return "field";
            if (_paramIndex.ContainsKey(id)) return "param";
            if (_localIndex.ContainsKey(id)) return "local";
        }

        string fieldKey = $"{_currentClassId}::{text}";
        if (_fieldIndex.ContainsKey(fieldKey)) return "field";
        if (_propIndex.ContainsKey(fieldKey)) return "property";

        return "unknown";
    }

    // ── Inter-method linking ──────────────────────────────────────────────────

    /// <summary>
    /// After all units are built, match call nodes to method nodes by name.
    /// Also emits global inter-method and inter-class edges.
    /// </summary>
    private void LinkInterMethodEdges()
    {
        // Build a flat index: methodName → [methodId] (handles overloads)
        var nameIndex = new Dictionary<string, List<string>>();
        foreach (var (id, m) in _methodIndex)
        {
            if (!nameIndex.ContainsKey(m.Name)) nameIndex[m.Name] = new();
            nameIndex[m.Name].Add(id);
        }

        foreach (var unit in _graph.Units)
        {
            foreach (var method in unit.Methods.Concat(unit.Constructors))
            {
                foreach (var call in method.Calls)
                {
                    // Try to resolve the call to a method in our graph
                    if (nameIndex.TryGetValue(call.MethodName, out var candidates))
                    {
                        // Pick best match (single match, or same class first)
                        string? resolvedId = candidates.Count == 1
                            ? candidates[0]
                            : candidates.FirstOrDefault(c => c.StartsWith(unit.Id + "::")) ?? candidates[0];

                        call.ResolvedMethodId = resolvedId;

                        // Global inter-method edge
                        _graph.FlowEdges.Add(new FlowEdge
                        {
                            From = call.Id,
                            To = resolvedId,
                            Kind = call.Receiver != null && !call.Receiver.StartsWith("_") && call.ReceiverKind != "field"
                                ? "inter-method"
                                : "inter-class",
                            Label = $"{method.Name} → {call.MethodName}"
                        });

                        // Argument → param binding
                        if (_methodIndex.TryGetValue(resolvedId, out var targetMethod))
                        {
                            for (int i = 0; i < call.Arguments.Count && i < targetMethod.Params.Count; i++)
                            {
                                var arg = call.Arguments[i];
                                var param = targetMethod.Params[i];
                                if (arg.SourceId != null)
                                    _graph.FlowEdges.Add(new FlowEdge
                                    {
                                        From = arg.SourceId,
                                        To = param.Id,
                                        Kind = "argument",
                                        Label = $"→ {param.Name}"
                                    });
                            }
                        }
                    }

                    // Field-receiver calls generate inter-class edges
                    if (call.ReceiverKind == "field" && call.Receiver != null)
                    {
                        string fieldKey = $"{unit.Id}::{call.Receiver}";
                        if (_fieldIndex.TryGetValue(fieldKey, out var field))
                        {
                            _graph.FlowEdges.Add(new FlowEdge
                            {
                                From = field.Id,
                                To = call.Id,
                                Kind = "inter-class",
                                Label = $"{field.Name}.{call.MethodName}"
                            });
                        }
                    }
                }
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string GetAccessibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PublicKeyword)))    return "public";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword) &&
                               modifiers.Any(m2 => m2.IsKind(SyntaxKind.InternalKeyword)))) return "protected internal";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.PrivateKeyword) &&
                               modifiers.Any(m2 => m2.IsKind(SyntaxKind.ProtectedKeyword)))) return "private protected";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.ProtectedKeyword))) return "protected";
        if (modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword)))  return "internal";
        return "private";
    }
}
