using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Unified walker that builds both <see cref="FlowGraph"/> and <see cref="MutationGraph"/>
/// in a single pass using Roslyn's IOperation tree and ControlFlowGraph.
/// Replaces the old DataFlowWalker + MutationWalker + FlowEnricher trio.
/// </summary>
internal sealed class OperationWalker
{
    private readonly SemanticModel _model;
    private readonly CSharpCompilation _compilation;
    private readonly string _sourceFile;

    private readonly FlowGraph _flow;
    private readonly MutationGraph _mutation = new();
    private readonly SymbolResolver _resolver = new();

    // Mutation sequence counter
    private int _mutSeq;
    private string NextMutId() => $"mut#{_mutSeq++}";

    // Statement sequence counter (per method, reset on method entry)
    private int _stmtSeq;

    // Collection mutators — same set as the old MutationWalker
    private static readonly HashSet<string> CollectionMutators = new(StringComparer.Ordinal)
    {
        "Add","AddRange","TryAdd","Insert","InsertRange",
        "Remove","RemoveAt","RemoveAll","RemoveRange","RemoveWhere","TryRemove",
        "Clear","Push","Pop","Enqueue","Dequeue","Set","TryUpdate",
        "Sort","Reverse","Shuffle"
    };

    // LINQ methods for chain detection
    private static readonly HashSet<string> LinqMethods = new(StringComparer.Ordinal)
    {
        "Select","Where","OrderBy","OrderByDescending","ThenBy","ThenByDescending",
        "GroupBy","Join","GroupJoin","SelectMany","Distinct","DistinctBy",
        "Take","TakeWhile","Skip","SkipWhile","First","FirstOrDefault",
        "Single","SingleOrDefault","Last","LastOrDefault","Any","All","Count",
        "Sum","Min","Max","Average","Aggregate","ToList","ToArray","ToDictionary",
        "ToHashSet","ToLookup","AsEnumerable","AsQueryable","Cast","OfType",
        "Zip","Append","Prepend","Reverse","Concat","Union","Intersect","Except"
    };

    // Track call nodes that are LINQ for chain assembly at end of method
    private readonly List<CallNode> _linqCalls = new();

    internal OperationWalker(SemanticModel model, CSharpCompilation compilation, string sourceFile)
    {
        _model = model;
        _compilation = compilation;
        _sourceFile = sourceFile;
        _flow = new FlowGraph { Source = sourceFile };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Public entry
    // ═══════════════════════════════════════════════════════════════════════════

    internal (FlowGraph flow, MutationGraph mutation) Walk()
    {
        var root = _model.SyntaxTree.GetRoot();

        // Ask the compiler for all named types declared in this syntax tree.
        // This catches classes, structs, records, interfaces, enums, and delegates
        // without assuming specific syntax node types.
        var typeSymbols = root.DescendantNodes()
            .Select(n => _model.GetDeclaredSymbol(n))
            .OfType<INamedTypeSymbol>()
            .Where(t => !t.IsImplicitlyDeclared)
            .Distinct<INamedTypeSymbol>(SymbolEqualityComparer.Default);

        foreach (var typeSymbol in typeSymbols)
            ProcessTypeFromSymbol(typeSymbol);

        // Handle top-level statements (modern C# Program.cs without a class).
        ProcessTopLevelStatements(root);

        BuildStateChangeSummaries();
        return (_flow, _mutation);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Type-level processing — compiler-driven
    // ═══════════════════════════════════════════════════════════════════════════

    private void ProcessTypeFromSymbol(INamedTypeSymbol typeSymbol)
    {
        string ns = typeSymbol.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>") ns = "";
        string className = typeSymbol.Name;
        string classId = _resolver.EnterClass(ns, className);

        var unit = new ClassUnit
        {
            Id = classId,
            Name = className,
            Namespace = ns.Length > 0 ? ns : null,
            Kind = typeSymbol.TypeKind switch
            {
                TypeKind.Interface => "interface",
                TypeKind.Struct => typeSymbol.IsRecord ? "record-struct" : "struct",
                TypeKind.Enum => "enum",
                TypeKind.Delegate => "delegate",
                _ => typeSymbol.IsRecord ? "record" : "class"
            }
        };

        // Attributes
        var classAttrs = typeSymbol.GetAttributes();
        if (classAttrs.Length > 0)
            unit.Attributes = classAttrs
                .Select(a => a.AttributeClass?.ToDisplayString() ?? a.ToString())
                .Where(a => !string.IsNullOrEmpty(a))
                .Select(a => a!)
                .ToList();

        // Base types — from the symbol, not syntax BaseList
        if (typeSymbol.BaseType != null && typeSymbol.BaseType.SpecialType != SpecialType.System_Object
            && typeSymbol.BaseType.SpecialType != SpecialType.System_ValueType)
            unit.BaseTypes.Add(typeSymbol.BaseType.ToDisplayString());
        foreach (var iface in typeSymbol.Interfaces)
            unit.BaseTypes.Add(iface.ToDisplayString());

        // Fields
        foreach (var member in typeSymbol.GetMembers().OfType<IFieldSymbol>())
        {
            if (member.IsImplicitlyDeclared) continue;
            string fieldId = _resolver.ResolveField(member);
            unit.Fields.Add(new FieldNode
            {
                Id = fieldId,
                Name = member.Name,
                Type = member.Type.ToDisplayString(),
                Accessibility = member.DeclaredAccessibility.ToString().ToLowerInvariant(),
                IsReadonly = member.IsReadOnly,
                IsStatic = member.IsStatic,
                Initializer = member.DeclaringSyntaxReferences
                    .Select(r => r.GetSyntax())
                    .OfType<VariableDeclaratorSyntax>()
                    .FirstOrDefault()?.Initializer?.Value?.ToString()
            });
            RegisterSymbol(fieldId, member.Name, member.Type, "field",
                isShared: true, isReadonly: member.IsReadOnly);
        }

        // Properties
        foreach (var member in typeSymbol.GetMembers().OfType<IPropertySymbol>())
        {
            if (member.IsImplicitlyDeclared) continue;
            string propId = _resolver.ResolveProperty(member);
            var propSyntax = member.DeclaringSyntaxReferences
                .Select(r => r.GetSyntax()).OfType<PropertyDeclarationSyntax>().FirstOrDefault();
            bool isAuto = propSyntax?.AccessorList?.Accessors
                .All(a => a.Body == null && a.ExpressionBody == null) ?? false;

            unit.Properties.Add(new PropertyNode
            {
                Id = propId,
                Name = member.Name,
                Type = member.Type.ToDisplayString(),
                Accessibility = member.DeclaredAccessibility.ToString().ToLowerInvariant(),
                HasGetter = member.GetMethod != null,
                HasSetter = member.SetMethod != null,
                IsAutoProperty = isAuto
            });
            RegisterSymbol(propId, member.Name, member.Type, "property", isShared: true);
        }

        // Methods — ask the compiler for all IMethodSymbol members.
        // This catches constructors, regular methods, operators, conversions,
        // finalizers, and primary constructor-generated members.
        foreach (var member in typeSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            if (member.IsImplicitlyDeclared) continue;

            // Skip property accessors and event accessors — they're modeled via
            // the property/field, not as standalone methods.
            if (member.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet
                or MethodKind.EventAdd or MethodKind.EventRemove
                or MethodKind.DelegateInvoke)
                continue;

            var bodySyntax = GetMethodBodySyntax(member);
            bool isCtor = member.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor;

            string methodId = isCtor
                ? _resolver.EnterConstructor()
                : _resolver.EnterMethod(member.Name);

            string name = isCtor ? ".ctor" : member.Name;
            var node = BuildMethodNode(methodId, name, member.ReturnType.ToDisplayString(), member);

            var methodAttrs = member.GetAttributes();
            if (methodAttrs.Length > 0)
                node.Attributes = methodAttrs
                    .Select(a => a.AttributeClass?.ToDisplayString() ?? a.ToString())
                    .Where(a => !string.IsNullOrEmpty(a))
                    .Select(a => a!)
                    .ToList();

            ProcessMethodBody(bodySyntax, node);

            if (isCtor)
                unit.Constructors.Add(node);
            else
                unit.Methods.Add(node);
        }

        _flow.Units.Add(unit);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Top-level statements
    // ═══════════════════════════════════════════════════════════════════════════

    private void ProcessTopLevelStatements(SyntaxNode root)
    {
        var globalStatements = root.ChildNodes().OfType<GlobalStatementSyntax>().ToList();
        if (globalStatements.Count == 0) return;

        // The compiler synthesizes a Program class with a <Main>$ method.
        // We surface it as a real ClassUnit so it appears in the output.
        string classId = _resolver.EnterClass("", "Program");

        // Guard against duplicate if user also defined a partial class Program
        if (_flow.Units.Any(u => u.Id == classId)) return;

        var unit = new ClassUnit
        {
            Id = classId,
            Name = "Program",
            Kind = "class"
        };

        string methodId = _resolver.EnterMethod("<Main>$");
        var methodNode = new MethodNode
        {
            Id = methodId,
            Name = "<Main>$",
            ReturnType = "void",
            Accessibility = "private",
            IsAsync = false,
            IsStatic = true
        };

        // Detect async from presence of await expressions in top-level statements
        methodNode.IsAsync = globalStatements
            .Any(gs => gs.DescendantNodes().OfType<AwaitExpressionSyntax>().Any());

        // Walk each global statement's IOperation tree
        _stmtSeq = 0;
        _linqCalls.Clear();

        // Try to build CFG from the entry point's body for guard/loop context
        ControlFlowMapper? cfgMapper = null;
        var entryPoint = _compilation.GetEntryPoint(CancellationToken.None);
        if (entryPoint != null)
        {
            var entryBody = GetMethodBodySyntax(entryPoint);
            if (entryBody != null)
            {
                var entryOp = _model.GetOperation(entryBody);
                if (entryOp is IBlockOperation blockOp)
                {
                    try
                    {
                        var cfg = ControlFlowGraph.Create(blockOp);
                        cfgMapper = new ControlFlowMapper(cfg);
                    }
                    catch (InvalidOperationException) { /* CFG not available for this body form */ }
                }
            }
        }

        foreach (var globalStmt in globalStatements)
        {
            var op = _model.GetOperation(globalStmt.Statement);
            if (op != null)
                WalkOperation(op, methodNode, cfgMapper);
        }

        AssembleLinqChains(methodNode);
        LinkCallEdges(methodNode);

        unit.Methods.Add(methodNode);
        _flow.Units.Add(unit);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Method body access — from IMethodSymbol to syntax body
    // ═══════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Resolves an <see cref="IMethodSymbol"/> to the syntax node representing its body.
    /// Works for regular methods, constructors, operators, conversions, and finalizers.
    /// Returns null for abstract/extern/partial methods with no body.
    /// </summary>
    private SyntaxNode? GetMethodBodySyntax(IMethodSymbol method)
    {
        var currentTree = _model.SyntaxTree;
        foreach (var syntaxRef in method.DeclaringSyntaxReferences)
        {
            // Only consider syntax nodes that belong to the current tree.
            // Partial classes may have members declared in other files.
            if (syntaxRef.SyntaxTree != currentTree) continue;

            var syntax = syntaxRef.GetSyntax();
            var result = syntax switch
            {
                MethodDeclarationSyntax m           => (SyntaxNode?)m.Body ?? m.ExpressionBody,
                ConstructorDeclarationSyntax c      => (SyntaxNode?)c.Body ?? c.ExpressionBody,
                DestructorDeclarationSyntax d       => (SyntaxNode?)d.Body ?? d.ExpressionBody,
                OperatorDeclarationSyntax o         => (SyntaxNode?)o.Body ?? o.ExpressionBody,
                ConversionOperatorDeclarationSyntax co => (SyntaxNode?)co.Body ?? co.ExpressionBody,
                ArrowExpressionClauseSyntax arrow    => arrow,
                _ => null
            };
            if (result != null) return result;
        }
        return null;
    }

    private MethodNode BuildMethodNode(string methodId, string name, string returnType,
        IMethodSymbol methodSymbol)
    {
        var node = new MethodNode
        {
            Id = methodId,
            Name = name,
            ReturnType = returnType,
            Accessibility = methodSymbol.DeclaredAccessibility.ToString().ToLowerInvariant(),
            IsAsync = methodSymbol.IsAsync,
            IsStatic = methodSymbol.IsStatic
        };

        // Params — use IParameterSymbol for exact type + ref kind
        foreach (var param in methodSymbol.Parameters)
        {
            string paramId = _resolver.ResolveParam(param);
            string? modifier = param.RefKind switch
            {
                RefKind.Ref => "ref",
                RefKind.Out => "out",
                RefKind.In => "in",
                _ => null
            };
            node.Params.Add(new ParamNode
            {
                Id = paramId,
                Name = param.Name,
                Type = param.Type.ToDisplayString(),
                HasDefault = param.HasExplicitDefaultValue,
                DefaultValue = param.HasExplicitDefaultValue ? param.ExplicitDefaultValue?.ToString() : null,
                Modifier = modifier
            });
            RegisterSymbol(paramId, param.Name, param.Type, "param",
                isShared: param.RefKind is RefKind.Ref or RefKind.Out,
                declaredIn: methodId);
        }

        return node;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Method body processing via IOperation + ControlFlowGraph
    // ═══════════════════════════════════════════════════════════════════════════

    private void ProcessMethodBody(SyntaxNode? bodySyntax, MethodNode node)
    {
        _stmtSeq = 0;
        _linqCalls.Clear();

        if (bodySyntax == null) return;

        var bodyOp = _model.GetOperation(bodySyntax);
        if (bodyOp == null) return;

        // Build CFG for guard/loop context
        ControlFlowMapper? cfgMapper = null;
        if (bodyOp is IBlockOperation blockOp)
        {
            try
            {
                var cfg = ControlFlowGraph.Create(blockOp);
                cfgMapper = new ControlFlowMapper(cfg);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            { /* CFG not available — non-root block, partial code, etc. */ }
        }

        // Walk the IOperation tree
        WalkOperation(bodyOp, node, cfgMapper);

        // Assemble LINQ chains from collected LINQ call nodes
        AssembleLinqChains(node);

        // Link inter-method edges for calls in this method
        LinkCallEdges(node);
    }

    private void WalkOperation(IOperation operation, MethodNode method, ControlFlowMapper? cfgMapper)
    {
        switch (operation)
        {
            case IVariableDeclarationGroupOperation declGroup:
                foreach (var decl in declGroup.Declarations)
                    foreach (var declarator in decl.Declarators)
                        ProcessLocalDeclaration(declarator, method, cfgMapper);
                return; // children already processed

            case ISimpleAssignmentOperation assign:
                ProcessAssignment(assign, method, cfgMapper);
                return;

            case ICompoundAssignmentOperation compAssign:
                ProcessCompoundAssignment(compAssign, method, cfgMapper);
                return;

            case IInvocationOperation invocation:
                ProcessInvocation(invocation, method, cfgMapper, resultAssignedTo: null, resultAssignedToId: null);
                return;

            case IReturnOperation ret:
                ProcessReturn(ret, method, cfgMapper);
                return;

            case IForEachLoopOperation forEach:
                ProcessForEach(forEach, method, cfgMapper);
                return;
        }

        // Default: recurse into children
        foreach (var child in operation.ChildOperations)
            WalkOperation(child, method, cfgMapper);
    }

    // ── Local declaration ────────────────────────────────────────────────────

    private void ProcessLocalDeclaration(IVariableDeclaratorOperation declarator,
        MethodNode method, ControlFlowMapper? cfgMapper)
    {
        var localSymbol = declarator.Symbol;
        string localId = _resolver.ResolveLocal(localSymbol);
        var typeInfo = TypeClassifier.Classify(localSymbol.Type);

        var initOp = declarator.Initializer?.Value;
        string? initExpr = initOp?.Syntax?.ToString();
        var dataSources = new List<string>();

        if (initOp != null)
            dataSources = CollectSourceIds(initOp);

        // Conditional init detection
        bool hasConditionalInit = false;
        string? conditionalKind = null;
        if (initOp is IConditionalOperation) { hasConditionalInit = true; conditionalKind = "ternary"; }
        else if (initOp is ICoalesceOperation) { hasConditionalInit = true; conditionalKind = "null-coalescing"; }
        else if (initOp is ISwitchExpressionOperation) { hasConditionalInit = true; conditionalKind = "switch-expression"; }

        // Object initializer decomposition
        List<ObjectInitAssignment>? objectInits = null;
        if (initOp is IObjectCreationOperation objCreate && objCreate.Initializer != null)
        {
            objectInits = new();
            foreach (var init in objCreate.Initializer.Initializers)
            {
                if (init is ISimpleAssignmentOperation initAssign)
                {
                    string propName = initAssign.Target.Syntax?.ToString() ?? "";
                    string valExpr = initAssign.Value.Syntax?.ToString() ?? "";
                    objectInits.Add(new ObjectInitAssignment { Property = propName, ValueExpression = valExpr });
                }
            }
            if (objectInits.Count == 0) objectInits = null;
        }

        var localNode = new LocalNode
        {
            Id = localId,
            Name = localSymbol.Name,
            Type = localSymbol.Type.ToDisplayString(),
            IsVar = declarator.Syntax is VariableDeclaratorSyntax vds &&
                    vds.Parent is VariableDeclarationSyntax vd && vd.Type.IsVar,
            InitExpression = initExpr,
            DataSourceIds = dataSources,
            HasConditionalInit = hasConditionalInit,
            ConditionalKind = conditionalKind,
            ObjectInitAssignments = objectInits
        };
        method.Locals.Add(localNode);

        RegisterSymbol(localId, localSymbol.Name, localSymbol.Type, "local",
            isShared: false, declaredIn: _resolver.CurrentMethodId);

        // Flow edges: each source → local
        foreach (var srcId in dataSources)
            method.FlowEdges.Add(new FlowEdge { From = srcId, To = localId, Kind = "assignment" });

        // If init is a call, extract it
        if (initOp is IInvocationOperation invInit)
            ProcessInvocation(invInit, method, cfgMapper, localSymbol.Name, localId);
        else if (initOp is IAwaitOperation { Operation: IInvocationOperation awaitedInv })
            ProcessInvocation(awaitedInv, method, cfgMapper, localSymbol.Name, localId);
    }

    // ── Assignment ───────────────────────────────────────────────────────────

    private void ProcessAssignment(ISimpleAssignmentOperation assign,
        MethodNode method, ControlFlowMapper? cfgMapper)
    {
        string target = assign.Target.Syntax?.ToString() ?? "";
        string assignId = _resolver.NextAssignmentId(target);
        string? targetId = ResolveOperandId(assign.Target);
        var sources = CollectSourceIds(assign.Value);

        var aNode = new AssignmentNode
        {
            Id = assignId,
            Target = target,
            TargetId = targetId,
            Expression = assign.Value.Syntax?.ToString() ?? "",
            Operator = "=",
            SourceIds = sources,
            SequenceOrder = _stmtSeq++
        };

        // Tag target kind using IOperation type
        aNode.TargetKind = assign.Target switch
        {
            IFieldReferenceOperation => "field",
            IPropertyReferenceOperation => "property",
            ILocalReferenceOperation => "local",
            _ => null
        };

        method.Assignments.Add(aNode);

        foreach (var srcId in sources)
            method.FlowEdges.Add(new FlowEdge { From = srcId, To = targetId ?? assignId, Kind = "assignment" });
        if (targetId != null)
        {
            string edgeKind = aNode.TargetKind == "property" ? "property-write" : "field-write";
            method.FlowEdges.Add(new FlowEdge { From = assignId, To = targetId, Kind = edgeKind, Label = target });
        }

        // Mutation detection
        EmitAssignmentMutation(assign, targetId, target, method, cfgMapper);

        // Extract calls from RHS
        foreach (var inv in assign.Value.DescendantsAndSelf().OfType<IInvocationOperation>())
            ProcessInvocation(inv, method, cfgMapper, target, targetId);
    }

    private void ProcessCompoundAssignment(ICompoundAssignmentOperation assign,
        MethodNode method, ControlFlowMapper? cfgMapper)
    {
        string target = assign.Target.Syntax?.ToString() ?? "";
        string assignId = _resolver.NextAssignmentId(target);
        string? targetId = ResolveOperandId(assign.Target);
        var sources = CollectSourceIds(assign.Value);
        if (targetId != null && !sources.Contains(targetId)) sources.Add(targetId);

        method.Assignments.Add(new AssignmentNode
        {
            Id = assignId,
            Target = target,
            TargetId = targetId,
            Expression = assign.Value.Syntax?.ToString() ?? "",
            Operator = assign.OperatorKind.ToString(),
            SourceIds = sources,
            SequenceOrder = _stmtSeq++
        });

        foreach (var srcId in sources)
            method.FlowEdges.Add(new FlowEdge { From = srcId, To = targetId ?? assignId, Kind = "assignment" });
    }

    // ── Invocation ───────────────────────────────────────────────────────────

    private void ProcessInvocation(IInvocationOperation invocation,
        MethodNode method, ControlFlowMapper? cfgMapper,
        string? resultAssignedTo, string? resultAssignedToId)
    {
        string callText = invocation.Syntax.ToString();
        string callId = _resolver.NextCallId(callText);
        var targetMethod = invocation.TargetMethod;

        string? receiver = null;
        string? receiverKind = null;
        if (invocation.Instance != null)
        {
            receiver = invocation.Instance.Syntax?.ToString();
            receiverKind = ClassifyReceiverOp(invocation.Instance);
        }

        bool isAwaited = invocation.Parent is IAwaitOperation ||
                         invocation.Syntax.Parent is AwaitExpressionSyntax;
        bool isChained = invocation.Instance is IInvocationOperation;

        var callNode = new CallNode
        {
            Id = callId,
            Expression = callText.Length > 80 ? callText[..80] + "…" : callText,
            MethodName = targetMethod.Name,
            Receiver = receiver,
            ReceiverKind = receiverKind,
            IsAwaited = isAwaited,
            IsChained = isChained,
            ResultAssignedTo = resultAssignedTo,
            ResultAssignedToId = resultAssignedToId,
            ResolvedMethodId = _resolver.Lookup(targetMethod) ??
                BuildExternalMethodId(targetMethod),
            SequenceOrder = _stmtSeq++
        };

        // Arguments
        int argIdx = 0;
        foreach (var arg in invocation.Arguments)
        {
            string? argMod = arg.Parameter?.RefKind switch
            {
                RefKind.Ref => "ref",
                RefKind.Out => "out",
                RefKind.In => "in",
                _ => null
            };

            var argNode = new ArgumentNode
            {
                Index = argIdx++,
                Name = arg.Parameter?.Name,
                Expression = arg.Value.Syntax?.ToString() ?? "",
                SourceId = ResolveOperandId(arg.Value),
                Modifier = argMod
            };
            callNode.Arguments.Add(argNode);

            if (argNode.SourceId != null)
                method.FlowEdges.Add(new FlowEdge
                {
                    From = argNode.SourceId, To = callId, Kind = "argument",
                    Label = $"arg[{argNode.Index}]"
                });
        }

        // Result → local edge
        if (resultAssignedToId != null)
            method.FlowEdges.Add(new FlowEdge
            {
                From = callId, To = resultAssignedToId, Kind = "inter-method", Label = "return value"
            });

        method.Calls.Add(callNode);

        // Track LINQ calls for chain assembly
        if (LinqMethods.Contains(targetMethod.Name))
            _linqCalls.Add(callNode);

        // Mutation detection for invocations
        EmitInvocationMutation(invocation, callId, method, cfgMapper);
    }

    // ── Return ───────────────────────────────────────────────────────────────

    private void ProcessReturn(IReturnOperation ret, MethodNode method, ControlFlowMapper? cfgMapper)
    {
        string returnId = _resolver.NextReturnId();
        var returnedValue = ret.ReturnedValue;

        var retNode = new ReturnNode
        {
            Id = returnId,
            Expression = returnedValue?.Syntax?.ToString(),
            SequenceOrder = _stmtSeq++
        };

        if (returnedValue != null)
        {
            retNode.SourceIds = CollectSourceIds(returnedValue);
            foreach (var srcId in retNode.SourceIds)
                method.FlowEdges.Add(new FlowEdge { From = srcId, To = returnId, Kind = "return" });

            // Tag interpolated strings
            if (returnedValue is IInterpolatedStringOperation)
                retNode.IsInterpolated = true;
        }

        method.Returns.Add(retNode);
    }

    // ── ForEach ──────────────────────────────────────────────────────────────

    private void ProcessForEach(IForEachLoopOperation forEach, MethodNode method, ControlFlowMapper? cfgMapper)
    {
        // The iteration variable
        if (forEach.LoopControlVariable is IVariableDeclaratorOperation iterDecl)
        {
            var localSymbol = iterDecl.Symbol;
            string localId = _resolver.ResolveLocal(localSymbol);
            var collSources = CollectSourceIds(forEach.Collection);

            method.Locals.Add(new LocalNode
            {
                Id = localId,
                Name = localSymbol.Name,
                Type = localSymbol.Type.ToDisplayString(),
                IsVar = iterDecl.Syntax is VariableDeclaratorSyntax vs &&
                        vs.Parent is VariableDeclarationSyntax vds && vds.Type.IsVar,
                InitExpression = $"foreach element of {forEach.Collection.Syntax}",
                DataSourceIds = collSources
            });
            RegisterSymbol(localId, localSymbol.Name, localSymbol.Type, "local",
                isShared: false, declaredIn: _resolver.CurrentMethodId);

            foreach (var src in collSources)
                method.FlowEdges.Add(new FlowEdge { From = src, To = localId, Kind = "assignment", Label = "foreach" });
        }

        // Walk the loop body
        WalkOperation(forEach.Body, method, cfgMapper);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Mutation detection
    // ═══════════════════════════════════════════════════════════════════════════

    private void EmitAssignmentMutation(ISimpleAssignmentOperation assign,
        string? targetId, string target, MethodNode method, ControlFlowMapper? cfgMapper)
    {
        ISymbol? targetSymbol = GetReferencedSymbol(assign.Target);
        if (targetSymbol == null) return;

        var typeInfo = TypeClassifier.Classify(targetSymbol switch
        {
            IFieldSymbol f => f.Type,
            IPropertySymbol p => p.Type,
            IParameterSymbol p => p.Type,
            ILocalSymbol l => l.Type,
            _ => null
        });

        // Determine if this is a mutation (reference-type shared symbol or member access)
        bool isMemberAccess = assign.Target is IPropertyReferenceOperation or IFieldReferenceOperation;
        bool isShared = targetSymbol != null && TypeClassifier.IsShared(targetSymbol);

        if (!isMemberAccess && !isShared && typeInfo.TypeKind != "reference") return;

        string kind = assign.Target switch
        {
            IPropertyReferenceOperation => "property-set",
            IFieldReferenceOperation => "field-write",
            IArrayElementReferenceOperation => "index-write",
            _ => "field-write"
        };

        string? memberName = assign.Target switch
        {
            IPropertyReferenceOperation pr => pr.Property.Name,
            IFieldReferenceOperation fr => fr.Field.Name,
            _ => null
        };

        // Receiver symbol for member access
        string? receiverSymId = targetId;
        if (assign.Target is IMemberReferenceOperation memberRef && memberRef.Instance != null)
            receiverSymId = ResolveOperandId(memberRef.Instance) ?? targetId;

        var guard = cfgMapper?.GetGuard(assign);
        bool inLoop = cfgMapper?.IsInsideLoop(assign) ?? false;

        EmitMutation(new MutationNode
        {
            Id = NextMutId(),
            Kind = kind,
            TargetSymbolId = receiverSymId ?? target,
            TargetMember = memberName,
            SourceExpression = assign.Value.Syntax?.ToString() ?? "",
            SourceIds = CollectSourceIds(assign.Value),
            MethodId = method.Id,
            IsOnSharedObject = isShared,
            IsInsideLoop = inLoop,
            Guard = guard,
            AffectedAliasIds = AliasIdsFor(receiverSymId)
        });
    }

    private void EmitInvocationMutation(IInvocationOperation invocation,
        string callId, MethodNode method, ControlFlowMapper? cfgMapper)
    {
        var targetMethod = invocation.TargetMethod;

        // Collection mutators
        if (CollectionMutators.Contains(targetMethod.Name) && invocation.Instance != null)
        {
            string? recvId = ResolveOperandId(invocation.Instance);
            if (recvId != null)
            {
                bool isShared = GetReferencedSymbol(invocation.Instance) is { } s && TypeClassifier.IsShared(s);
                string kind = targetMethod.Name switch
                {
                    "Add" or "AddRange" or "TryAdd" or "Insert" or "InsertRange" => "collection-add",
                    "Remove" or "RemoveAt" or "RemoveAll" or "RemoveRange" or "RemoveWhere" or "TryRemove" or "Clear" => "collection-remove",
                    "Sort" or "Reverse" or "Shuffle" => "collection-sort",
                    _ => "collection-add"
                };

                var guard = cfgMapper?.GetGuard(invocation);
                bool inLoop = cfgMapper?.IsInsideLoop(invocation) ?? false;

                EmitMutation(new MutationNode
                {
                    Id = NextMutId(),
                    Kind = kind,
                    TargetSymbolId = recvId,
                    TargetMember = targetMethod.Name,
                    SourceExpression = invocation.Syntax.ToString(),
                    SourceIds = invocation.Arguments
                        .Select(a => ResolveOperandId(a.Value))
                        .Where(id => id != null).Select(id => id!).ToList(),
                    MethodId = method.Id,
                    IsOnSharedObject = isShared,
                    IsInsideLoop = inLoop,
                    Guard = guard,
                    AffectedAliasIds = AliasIdsFor(recvId)
                });
                return;
            }
        }

        // External call on reference-type receiver
        if (invocation.Instance != null)
        {
            var recvSymbol = GetReferencedSymbol(invocation.Instance);
            if (recvSymbol != null)
            {
                var recvType = TypeClassifier.Classify(GetSymbolType(recvSymbol));
                if (recvType.TypeKind is "reference" or "interface" or "unknown" &&
                    TypeClassifier.IsShared(recvSymbol))
                {
                    string? recvId = ResolveOperandId(invocation.Instance);
                    var guard = cfgMapper?.GetGuard(invocation);
                    bool inLoop = cfgMapper?.IsInsideLoop(invocation) ?? false;

                    EmitMutation(new MutationNode
                    {
                        Id = NextMutId(),
                        Kind = "external-call",
                        TargetSymbolId = recvId ?? invocation.Instance.Syntax?.ToString() ?? "",
                        TargetMember = targetMethod.Name,
                        SourceExpression = invocation.Syntax.ToString(),
                        SourceIds = invocation.Arguments
                            .Select(a => ResolveOperandId(a.Value))
                            .Where(id => id != null).Select(id => id!).ToList(),
                        MethodId = method.Id,
                        IsOnSharedObject = true,
                        IsInsideLoop = inLoop,
                        Guard = guard,
                        AffectedAliasIds = AliasIdsFor(recvId)
                    });
                }
            }
        }

        // ref/out arguments
        foreach (var arg in invocation.Arguments)
        {
            if (arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out)
            {
                string? argSymId = ResolveOperandId(arg.Value);
                if (argSymId != null)
                {
                    var guard = cfgMapper?.GetGuard(invocation);
                    bool inLoop = cfgMapper?.IsInsideLoop(invocation) ?? false;

                    EmitMutation(new MutationNode
                    {
                        Id = NextMutId(),
                        Kind = "ref-write",
                        TargetSymbolId = argSymId,
                        SourceExpression = invocation.Syntax.ToString(),
                        SourceIds = new() { argSymId },
                        MethodId = method.Id,
                        IsOnSharedObject = GetReferencedSymbol(arg.Value) is { } argSym && TypeClassifier.IsShared(argSym),
                        IsInsideLoop = inLoop,
                        Guard = guard,
                        AffectedAliasIds = AliasIdsFor(argSymId)
                    });
                }
            }
        }
    }

    private void EmitMutation(MutationNode m) => _mutation.Mutations.Add(m);

    // ═══════════════════════════════════════════════════════════════════════════
    // Inter-method linking
    // ═══════════════════════════════════════════════════════════════════════════

    private void LinkCallEdges(MethodNode method)
    {
        foreach (var call in method.Calls)
        {
            if (call.ResolvedMethodId == null) continue;

            // Global inter-method / inter-class edge
            string edgeKind = call.ReceiverKind == "field" ? "inter-class" : "inter-method";
            _flow.FlowEdges.Add(new FlowEdge
            {
                From = call.Id,
                To = call.ResolvedMethodId,
                Kind = edgeKind,
                Label = $"{method.Name} → {call.MethodName}"
            });

            // Field-receiver → call edge
            if (call.ReceiverKind == "field" && call.Receiver != null)
            {
                var fieldId = ResolveNameToId(call.Receiver);
                if (fieldId != null)
                    _flow.FlowEdges.Add(new FlowEdge
                    {
                        From = fieldId, To = call.Id, Kind = "inter-class",
                        Label = $"{call.Receiver}.{call.MethodName}"
                    });
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // LINQ chain assembly
    // ═══════════════════════════════════════════════════════════════════════════

    private void AssembleLinqChains(MethodNode method)
    {
        if (_linqCalls.Count < 2) return;

        var chains = new List<LinqChain>();
        var assigned = new HashSet<string>();

        foreach (var terminal in _linqCalls.Where(c => !c.IsChained || c.ResultAssignedTo != null))
        {
            if (assigned.Contains(terminal.Id)) continue;
            var chain = _linqCalls
                .Where(c => !assigned.Contains(c.Id) && terminal.Expression.Contains(c.MethodName))
                .OrderBy(c => c.Expression.Length)
                .ToList();

            if (chain.Count >= 2)
            {
                chains.Add(new LinqChain
                {
                    Steps = chain.Select((c, i) => new LinqChainStep
                    {
                        Order = i, CallId = c.Id, MethodName = c.MethodName
                    }).ToList()
                });
                foreach (var c in chain) assigned.Add(c.Id);
            }
        }

        if (chains.Count > 0)
            method.LinqChains = chains;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Alias detection + State change summaries
    // ═══════════════════════════════════════════════════════════════════════════

    private void BuildStateChangeSummaries()
    {
        // Build aliases from flow edges
        BuildAliases();

        var byTarget = _mutation.Mutations
            .GroupBy(m => m.TargetSymbolId)
            .ToDictionary(g => g.Key, g => g.ToList());

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
            var sym = _mutation.Symbols.FirstOrDefault(s => s.Id == symId);
            if (sym == null) continue;

            var writtenFrom = mutations.Select(m => m.MethodId).Distinct().ToList();
            readByMethod.TryGetValue(symId, out var reads);

            _mutation.StateChangeSummaries.Add(new StateChangeSummary
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
                HasAliasedExternalCall = mutations
                    .Where(m => m.Kind == "external-call")
                    .Any(m => m.AffectedAliasIds.Count > 0)
            });
        }

        _mutation.StateChangeSummaries.Sort((a, b) =>
        {
            if (a.IsShared != b.IsShared) return a.IsShared ? -1 : 1;
            return b.Mutations.Count.CompareTo(a.Mutations.Count);
        });
    }

    private void BuildAliases()
    {
        var symById = new Dictionary<string, SymbolInfo>(_mutation.Symbols.Count);
        foreach (var s in _mutation.Symbols)
            symById.TryAdd(s.Id, s);

        foreach (var unit in _flow.Units)
            foreach (var method in unit.Methods.Concat(unit.Constructors))
                foreach (var local in method.Locals)
                {
                    if (!symById.TryGetValue(local.Id, out var localSym)) continue;
                    if (localSym.TypeKind is not ("reference" or "interface" or "unknown")) continue;

                    foreach (var srcId in local.DataSourceIds)
                    {
                        if (!symById.TryGetValue(srcId, out var srcSym)) continue;
                        if (srcSym.TypeKind is not ("reference" or "interface" or "unknown")) continue;

                        if (!srcSym.AliasIds.Contains(local.Id)) srcSym.AliasIds.Add(local.Id);
                        if (!localSym.AliasIds.Contains(srcId)) localSym.AliasIds.Add(srcId);
                    }
                }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private void RegisterSymbol(string id, string name, ITypeSymbol type, string symbolKind,
        bool isShared = false, bool isReadonly = false, string? declaredIn = null)
    {
        var ti = TypeClassifier.Classify(type);
        _mutation.Symbols.Add(new SymbolInfo
        {
            Id = id,
            Name = name,
            TypeName = type.ToDisplayString(),
            TypeKind = ti.TypeKind,
            SymbolKind = symbolKind,
            IsShared = isShared,
            IsReadonly = isReadonly,
            IsNullable = ti.IsNullable || ti.TypeKind is "reference" or "interface",
            NullableFlowState = ti.NullableFlowState,
            DeclaredInMethod = declaredIn
        });
    }

    /// <summary>Collect all symbol IDs referenced in an operation's sub-tree.</summary>
    private List<string> CollectSourceIds(IOperation operation)
    {
        var ids = new HashSet<string>();
        foreach (var desc in operation.DescendantsAndSelf())
        {
            var symbol = GetReferencedSymbol(desc);
            if (symbol != null)
            {
                var id = _resolver.ResolveAny(symbol);
                if (id != null) ids.Add(id);
            }
        }
        return ids.ToList();
    }

    /// <summary>Get the single symbol referenced by an operation (field, local, param, property).</summary>
    private static ISymbol? GetReferencedSymbol(IOperation operation) => operation switch
    {
        IFieldReferenceOperation f => f.Field,
        IPropertyReferenceOperation p => p.Property,
        ILocalReferenceOperation l => l.Local,
        IParameterReferenceOperation p => p.Parameter,
        IConversionOperation c => GetReferencedSymbol(c.Operand),
        IParenthesizedOperation p => GetReferencedSymbol(p.Operand),
        _ => null
    };

    /// <summary>Resolve an operation to its known node ID.</summary>
    private string? ResolveOperandId(IOperation operation)
    {
        var symbol = GetReferencedSymbol(operation);
        return _resolver.ResolveAny(symbol);
    }

    /// <summary>Resolve a name string to a known symbol ID (for receiver lookup).</summary>
    private string? ResolveNameToId(string name)
    {
        // Check registered symbols by name
        return _mutation.Symbols.FirstOrDefault(s => s.Name == name)?.Id;
    }

    private List<string> AliasIdsFor(string? symId)
    {
        if (symId == null) return new();
        var sym = _mutation.Symbols.FirstOrDefault(s => s.Id == symId);
        return sym?.AliasIds ?? new();
    }

    private static string ClassifyReceiverOp(IOperation receiver) => receiver switch
    {
        IFieldReferenceOperation => "field",
        IPropertyReferenceOperation => "property",
        IParameterReferenceOperation => "param",
        ILocalReferenceOperation => "local",
        IInstanceReferenceOperation => "this",
        _ => "unknown"
    };

    private static ITypeSymbol? GetSymbolType(ISymbol symbol) => symbol switch
    {
        IFieldSymbol f => f.Type,
        IPropertySymbol p => p.Type,
        IParameterSymbol p => p.Type,
        ILocalSymbol l => l.Type,
        _ => null
    };

    private string? BuildExternalMethodId(IMethodSymbol method)
    {
        // For methods defined in our compilation, build the ID
        var containingType = method.ContainingType;
        if (containingType == null) return null;

        string ns = containingType.ContainingNamespace?.ToDisplayString() ?? "";
        if (ns == "<global namespace>") ns = "";
        string classId = IdGen.Class(ns, containingType.Name);

        // Find overload index
        int overloadIdx = 0;
        foreach (var member in containingType.GetMembers(method.Name))
        {
            if (SymbolEqualityComparer.Default.Equals(member, method)) break;
            if (member is IMethodSymbol) overloadIdx++;
        }

        return method.MethodKind == MethodKind.Constructor
            ? IdGen.Constructor(classId, overloadIdx)
            : IdGen.Method(classId, method.Name, overloadIdx);
    }
}
