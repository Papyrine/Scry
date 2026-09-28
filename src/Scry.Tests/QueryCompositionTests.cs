using Microsoft.EntityFrameworkCore.Query;

/// <summary>
/// An operator is composed through a <c>Queryable</c> method closed over its type arguments once, and
/// a provider asked through its generic <c>CreateQuery</c> once per element type. A closing is keyed
/// by value, so two requests of one shape share it and two shapes differing in a type argument never
/// do; and what a provider is asked is the same query the untyped path produced.
/// </summary>
public class QueryCompositionTests
{
    [Test]
    public async Task AClosingIsSharedByShape()
    {
        var source = Expression.Constant(new List<Employee>().AsQueryable());

        var first = QueryComposition.Call("Where", [typeof(Employee)], source, Expression.Quote(True<Employee>()));
        var second = QueryComposition.Call("Where", [typeof(Employee)], source, Expression.Quote(True<Employee>()));

        await Assert.That(first.Method).IsEqualTo(second.Method);
    }

    [Test]
    public async Task ClosingsDifferingInATypeArgumentDoNotShare()
    {
        var employees = Expression.Constant(new List<Employee>().AsQueryable());
        var departments = Expression.Constant(new List<Department>().AsQueryable());

        var first = QueryComposition.Call("Where", [typeof(Employee)], employees, Expression.Quote(True<Employee>()));
        var second = QueryComposition.Call("Where", [typeof(Department)], departments, Expression.Quote(True<Department>()));

        await Assert.That(first.Method).IsNotEqualTo(second.Method);
    }

    [Test]
    public async Task AFourArgumentClosingIsKeyedOnEveryArgument()
    {
        var outer = new List<Employee>().AsQueryable();
        var inner = new List<Department>().AsQueryable();
        Expression<Func<Employee, int>> outerKey = _ => _.Id;
        Expression<Func<Department, int>> innerKey = _ => _.Id;
        Expression<Func<Employee, Department, object[]>> result = (e, d) => new object[] {e.Name, d.Name};
        Expression<Func<Employee, string>> outerName = _ => _.Name;
        Expression<Func<Department, string>> innerName = _ => _.Name;

        var byId = QueryComposition.Call(
            "Join",
            [typeof(Employee), typeof(Department), typeof(int), typeof(object[])],
            outer.Expression,
            inner.Expression,
            Expression.Quote(outerKey),
            Expression.Quote(innerKey),
            Expression.Quote(result));
        var byName = QueryComposition.Call(
            "Join",
            [typeof(Employee), typeof(Department), typeof(string), typeof(object[])],
            outer.Expression,
            inner.Expression,
            Expression.Quote(outerName),
            Expression.Quote(innerName),
            Expression.Quote(result));

        using (Assert.Multiple())
        {
            await Assert.That(byId.Method.GetGenericArguments()[2]).IsEqualTo(typeof(int));
            await Assert.That(byName.Method.GetGenericArguments()[2]).IsEqualTo(typeof(string));
        }
    }

    [Test]
    public async Task ComposesOverTheDatabaseProvider()
    {
        using var context = TestContext.CreateSeeded();
        IQueryable set = context.Set<Employee>();

        var call = QueryComposition.Call("Where", [typeof(Employee)], set.Expression, Expression.Quote(True<Employee>()));
        var composed = QueryComposition.Compose(set, call);

        using (Assert.Multiple())
        {
            await Assert.That(composed.ElementType).IsEqualTo(typeof(Employee));
            await Assert.That(composed.Expression).IsSameReferenceAs(call);
            await Assert.That(composed.Provider).IsAssignableTo<IAsyncQueryProvider>();
        }
    }

    // An ordering's call is typed as the ordered sequence, and that is the type the next ThenBy binds
    // against — carried on the expression, whichever wrapper the provider hands back.
    [Test]
    public async Task ComposesAnOrderingOverAnInMemorySource()
    {
        IQueryable rows = new List<Employee> {new() {Name = "b"}, new() {Name = "a"}}.AsQueryable();
        Expression<Func<Employee, string>> byName = _ => _.Name;

        var ordered = QueryComposition.Compose(
            rows,
            QueryComposition.Call("OrderBy", [typeof(Employee), typeof(string)], rows.Expression, Expression.Quote(byName)));

        using (Assert.Multiple())
        {
            await Assert.That(ordered.Expression.Type).IsEqualTo(typeof(IOrderedQueryable<Employee>));
            await Assert.That(ordered.Cast<Employee>().Select(_ => _.Name)).IsEquivalentTo(["a", "b"], CollectionOrdering.Matching);
        }
    }

    [Test]
    public async Task ClosesTypesOncePerClosing()
    {
        using (Assert.Multiple())
        {
            await Assert.That(QueryComposition.Close(typeof(Nullable<>), typeof(int))).IsEqualTo(typeof(int?));
            await Assert.That(QueryComposition.Close(typeof(IGrouping<,>), typeof(int), typeof(Employee))).IsEqualTo(typeof(IGrouping<int, Employee>));
            await Assert.That(QueryComposition.Close(typeof(Nullable<>), typeof(int))).IsSameReferenceAs(QueryComposition.Close(typeof(Nullable<>), typeof(int)));
        }
    }

    [Test]
    public async Task FoldsResolveOncePerMethodAndElement()
    {
        IQueryable values = new List<int> {1, 2}.AsQueryable();

        var sum = QueryComposition.Fold("Sum", generic: false, values);
        var again = QueryComposition.Fold("Sum", generic: false, values);
        var max = QueryComposition.Fold("Max", generic: true, values);

        using (Assert.Multiple())
        {
            await Assert.That(sum.Method).IsEqualTo(again.Method);
            await Assert.That(sum.Method.IsGenericMethod).IsFalse();
            await Assert.That(max.Method.GetGenericArguments()).IsEquivalentTo([typeof(int)], CollectionOrdering.Matching);
            await Assert.That(values.Provider.Execute(sum)).IsEqualTo(3);
        }
    }

    static Expression<Func<T, bool>> True<T>() =>
        _ => true;
}
