namespace CSharpDataFlowAnalyzer;

/// <summary>
/// Generates stable, human-readable IDs for each node in the flow graph.
/// Format: {ClassName}.{MethodName}.{kind}:{name}  (or shorter for top-level nodes)
/// </summary>
public static class IdGen
{
    public static string Class(string ns, string className)
        => ns is { Length: > 0 } ? $"{ns}.{className}" : className;

    public static string Field(string classId, string fieldName)
        => $"{classId}::field:{fieldName}";

    public static string Property(string classId, string propName)
        => $"{classId}::prop:{propName}";

    public static string Constructor(string classId, int overloadIndex)
        => $"{classId}::ctor[{overloadIndex}]";

    public static string Method(string classId, string methodName, int overloadIndex)
        => $"{classId}::{methodName}[{overloadIndex}]";

    public static string Param(string methodId, string paramName)
        => $"{methodId}/param:{paramName}";

    public static string Local(string methodId, string localName, int declIndex)
        => declIndex == 0 ? $"{methodId}/local:{localName}" : $"{methodId}/local:{localName}#{declIndex}";

    public static string Assignment(string methodId, string target, int index)
        => $"{methodId}/assign:{target}[{index}]";

    public static string Call(string methodId, string callExpr, int index)
        => $"{methodId}/call:{Slug(callExpr)}[{index}]";

    public static string Return(string methodId, int index)
        => $"{methodId}/return[{index}]";

    // Shorten long expressions to keep IDs readable
    private static string Slug(string expr)
    {
        var s = expr.Length > 40 ? expr[..40] : expr;
        return s.Replace(' ', '_').Replace('\n', '_');
    }
}
