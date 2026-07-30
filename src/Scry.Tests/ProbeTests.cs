using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;

[TestFixture]
public class ProbeTests
{
    [Test]
    public void Probe()
    {
        using var context = TestContext.CreateSeeded();

        // What Scry's ExpressionBuilder actually emits for a client-supplied constant.
        var parameter = Expression.Parameter(typeof(Employee), "e");
        var built = Expression.Lambda<Func<Employee, bool>>(
            Expression.Equal(
                Expression.Property(parameter, "Name"),
                Expression.Constant("O'Brien; DROP TABLE Orders --")),
            parameter);

        // For comparison: an ordinary captured variable, which EF is known to parameterize.
        var captured = "O'Brien; DROP TABLE Orders --";

        var builtSql = context.Employees.Where(built).ToQueryString();
        var capturedSql = context.Employees.Where(_ => _.Name == captured).ToQueryString();

        Assert.Fail($"BUILT>>> {builtSql.Replace("\r", " ").Replace("\n", " ")} <<<CAPTURED>>> {capturedSql.Replace("\r", " ").Replace("\n", " ")}");
    }
}
