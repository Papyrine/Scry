/// <summary>
/// Finds whether an expression reads a member the disclosure audit leaves out: a property of a row
/// read off it, by the member's name and the type of the row it was read from — or any property of
/// a row of a source left out whole.
/// </summary>
sealed class LeftOutReads(ScryDisclosureOptions settings) :
    ExpressionVisitor
{
    bool found;

    public static bool Find(Expression expression, ScryDisclosureOptions settings)
    {
        var reads = new LeftOutReads(settings);
        reads.Visit(expression);
        return reads.found;
    }

    public override Expression? Visit(Expression? node)
    {
        if (found)
        {
            return node;
        }

        return base.Visit(node);
    }

    protected override Expression VisitMember(MemberExpression node)
    {
        if (node is {Member: PropertyInfo property, Expression: { } owner} &&
            (settings.Excludes(owner.Type, property.Name) || settings.Excludes(owner.Type)))
        {
            found = true;
            return node;
        }

        return base.VisitMember(node);
    }
}
