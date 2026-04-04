using System.Linq;
using Microsoft.CodeAnalysis;

namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Classifies an <see cref="ITypeSymbol"/> using Roslyn's type system instead of
/// string-based heuristics.  Replaces the old <c>MutationWalker.ClassifyTypeKind</c>.
/// </summary>
internal static class TypeClassifier
{
    /// <summary>
    /// Returns (typeKind, isNullable, nullableFlowState) for a given type symbol.
    /// </summary>
    internal static TypeInfo Classify(ITypeSymbol? type)
    {
        if (type == null || type.TypeKind == TypeKind.Error)
            return new TypeInfo("unknown", false, null);

        bool isNullable = type.NullableAnnotation == NullableAnnotation.Annotated;
        string? flowState = null;

        // Unwrap Nullable<T> for value types
        if (type is INamedTypeSymbol named && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            isNullable = true;
            type = named.TypeArguments[0];
        }

        string typeKind = type switch
        {
            { IsValueType: true } => "value",
            { TypeKind: TypeKind.Interface } => "interface",
            { TypeKind: TypeKind.TypeParameter } => "interface", // generic T — unknown concrete
            { IsReferenceType: true } => "reference",
            _ => "unknown"
        };

        return new TypeInfo(typeKind, isNullable, flowState);
    }

    /// <summary>
    /// Returns (typeKind, isNullable, nullableFlowState) from an <see cref="IOperation"/>'s
    /// type, using nullable flow analysis state when available.
    /// </summary>
    internal static TypeInfo ClassifyOperation(IOperation operation)
    {
        var info = Classify(operation.Type);

        // Override with per-operation nullable flow state if the semantic model provided it
        var typeInfo = operation.SemanticModel?.GetTypeInfo(operation.Syntax);
        if (typeInfo?.Nullability.FlowState is { } state)
        {
            string? flowStr = state switch
            {
                NullableFlowState.NotNull => "notNull",
                NullableFlowState.MaybeNull => "maybeNull",
                _ => null
            };
            return info with { NullableFlowState = flowStr };
        }

        return info;
    }

    /// <summary>
    /// Determines if a symbol is "shared" — visible beyond a single call frame.
    /// Fields, statics, and ref/out params are shared.
    /// </summary>
    internal static bool IsShared(ISymbol symbol) => symbol switch
    {
        IFieldSymbol f => !f.IsConst,
        IPropertySymbol => true,
        IParameterSymbol p => p.RefKind is RefKind.Ref or RefKind.Out,
        _ => symbol.IsStatic
    };

    /// <summary>
    /// Checks if a type implements any mutable collection interface.
    /// </summary>
    internal static bool IsCollection(ITypeSymbol? type)
    {
        if (type == null) return false;
        return type.AllInterfaces.Any(i =>
            i.OriginalDefinition.ToDisplayString() is
                "System.Collections.Generic.ICollection<T>" or
                "System.Collections.Generic.IList<T>" or
                "System.Collections.Generic.IDictionary<TKey, TValue>" or
                "System.Collections.IList" or
                "System.Collections.IDictionary");
    }

    internal readonly record struct TypeInfo(
        string TypeKind,
        bool IsNullable,
        string? NullableFlowState);
}
