using System.Linq.Expressions;

/// <summary>
/// A lambda that binds a variable and reads it translates as the lambda that reads the bound
/// expression in its place.
/// </summary>
/// <remarks>
/// C# cannot write one — an expression lambda refuses a statement body — which is why the shapes here
/// are built by hand. F# writes them all the time: a <c>let</c> inside a query lambda is one, and the
/// F# compiler emits one of its own for an anonymous record whose fields are written out of declared
/// order, to keep them evaluating in the order written. Each is built the way that compiler builds it,
/// one block per binding with the next block as its value, and pinned to the request the plain
/// spelling sends.
/// </remarks>
[TestFixture]
public class LetBindingTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record Row(int Id, string Name);

    record Card(string Name, Detail Department);

    record Detail(string Name);
    // ReSharper restore NotAccessedPositionalProperty.Local

    // {| Name = e.Name; Id = e.Id |}: the fields are declared Id then Name and written the other way
    // round, so each is bound in the order written and the constructor reads them in the order declared.
    [Test]
    public void ABindingPerFieldInAProjection()
    {
        var row = Parameter();
        var name = Expression.Variable(typeof(string), "Name");
        var id = Expression.Variable(typeof(int), "Id");
        var body = Bind(
            name,
            Member(row, "Name"),
            Bind(id, Member(row, "Id"), New<Row>(id, name)));

        AssertSameRequest(
            Employees().Select(Lambda<Row>(body, row)),
            Employees().Select(_ => new Row(_.Id, _.Name)));
    }

    // let n = e.Name in n.StartsWith "Al" && n.Length > 2: the one binding read twice.
    [Test]
    public void ABindingReadTwiceInAPredicate()
    {
        var row = Parameter();
        var name = Expression.Variable(typeof(string), "n");
        var body = Bind(
            name,
            Member(row, "Name"),
            Expression.AndAlso(
                Expression.Call(name, "StartsWith", Type.EmptyTypes, Expression.Constant("Al")),
                Expression.GreaterThan(Member(name, "Length"), Expression.Constant(2))));

        AssertSameRequest(
            Employees().Where(Lambda<bool>(body, row)),
            Employees().Where(_ => _.Name.StartsWith("Al") && _.Name.Length > 2));
    }

    // A nested record written out of order binds inside the constructor argument it is passed as.
    [Test]
    public void ABindingInsideAConstructedMember()
    {
        var row = Parameter();
        var name = Expression.Variable(typeof(string), "Name");
        var body = New<Card>(
            Member(row, "Name"),
            Bind(name, Member(Member(row, "Department"), "Name"), New<Detail>(name)));

        AssertSameRequest(
            Employees().Select(Lambda<Card>(body, row)),
            Employees().Select(_ => new Card(_.Name, new(_.Department!.Name))));
    }

    // let d = e.Department in …: the bound expression is a navigation, and the member read off the
    // variable becomes the path read off the row.
    [Test]
    public void ABindingOfANavigation()
    {
        var row = Parameter();
        var department = Expression.Variable(typeof(Department), "d");
        var body = Bind(
            department,
            Member(row, "Department"),
            New<Row>(Member(row, "Id"), Member(department, "Name")));

        AssertSameRequest(
            Employees().Select(Lambda<Row>(body, row)),
            Employees().Select(_ => new Row(_.Id, _.Department!.Name)));
    }

    // A predicate handed to a terminal rather than captured in the query reaches the translator by a
    // different door, and is read through the same substitution.
    [Test]
    public void ABindingInATerminalPredicate()
    {
        var row = Parameter();
        var name = Expression.Variable(typeof(string), "n");
        var body = Bind(
            name,
            Member(row, "Name"),
            Expression.Call(name, "StartsWith", Type.EmptyTypes, Expression.Constant("Al")));

        Assert.That(
            Sent(_ => _.CountAsync(Lambda<bool>(body, row))),
            Is.EqualTo(Sent(_ => _.CountAsync(_ => _.Name.StartsWith("Al")))));
    }

    [Test]
    public void AStatementThatBindsNothing()
    {
        var row = Parameter();
        var name = Expression.Variable(typeof(string), "Name");
        var body = Expression.Block(
            typeof(Row),
            [name],
            Expression.Call(typeof(Console).GetMethod(nameof(Console.WriteLine), [typeof(string)])!, Expression.Constant("x")),
            Expression.Assign(name, Member(row, "Name")),
            New<Row>(Member(row, "Id"), name));

        var exception = Assert.Throws<NotSupportedException>(() => Employees().Select(Lambda<Row>(body, row)).ToScryRequest());

        Assert.That(exception!.Message, Does.StartWith("A block inside a query lambda may only bind variables"));
    }

    [Test]
    public void AVariableReadBeforeItIsBound()
    {
        var row = Parameter();
        var name = Expression.Variable(typeof(string), "Name");
        var body = Expression.Block(typeof(Row), [name], New<Row>(Member(row, "Id"), name));

        var exception = Assert.Throws<NotSupportedException>(() => Employees().Select(Lambda<Row>(body, row)).ToScryRequest());

        Assert.That(exception!.Message, Is.EqualTo("Variable 'Name' is read before anything is bound to it."));
    }

    static ParameterExpression Parameter() =>
        Expression.Parameter(typeof(Employee), "e");

    static MemberExpression Member(Expression instance, string name) =>
        Expression.Property(instance, name);

    static NewExpression New<T>(params Expression[] arguments) =>
        Expression.New(typeof(T).GetConstructors().Single(), arguments);

    // One block per binding, the way the F# compiler lays a let out: the variable, its assignment, and
    // then whatever reads it as the block's value.
    static BlockExpression Bind(ParameterExpression variable, Expression bound, Expression value) =>
        Expression.Block(value.Type, [variable], Expression.Assign(variable, bound), value);

    static Expression<Func<Employee, TResult>> Lambda<TResult>(Expression body, ParameterExpression row) =>
        Expression.Lambda<Func<Employee, TResult>>(body, row);

    static IQueryable<Employee> Employees() =>
        new ScryClient((_, _) => throw new InvalidOperationException("Never sent.")).Source<Employee>("Employee");

    static void AssertSameRequest<T>(IQueryable<T> bound, IQueryable<T> plain) =>
        Assert.That(Json(bound.ToScryRequest()), Is.EqualTo(Json(plain.ToScryRequest())));

    static string Json(QueryRequest request) =>
        JsonSerializer.Serialize(request, ScryJson.Options);

    // What a terminal was about to send, captured at the transport and stopped there.
    static string Sent(Func<IQueryable<Employee>, Task> terminal)
    {
        QueryRequest? sent = null;
        var client = new ScryClient(
            (request, _) =>
            {
                sent = request;
                throw new StopBeforeSending();
            });

        try
        {
            terminal(client.Source<Employee>("Employee")).GetAwaiter().GetResult();
        }
        catch (StopBeforeSending)
        {
        }

        Assert.That(sent, Is.Not.Null);
        return Json(sent!);
    }

    sealed class StopBeforeSending :
        Exception;
}
