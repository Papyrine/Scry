namespace Scry.Wire;

/// <summary>
/// A navigation path of allow-listed property names, e.g. <c>["Manager", "Name"]</c>. Each segment
/// is validated against the allow-list of the type reached so far.
/// </summary>
public sealed record MemberExpr(IReadOnlyList<string> Path) :
    Expr;