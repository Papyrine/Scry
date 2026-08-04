[TestFixture]
public class AnalyzerTests
{
    [Test]
    public Task UnsupportedOperators() =>
        Verify(
            Analyze(
                """
                await Query.Order.SkipWhile(_ => _.Amount > 0).ToListAsync();
                await Query.Order.TakeWhile(_ => _.Amount > 0).ToListAsync();
                await Query.Order.DefaultIfEmpty().ToListAsync();
                await Query.Order.Append(null!).ToListAsync();
                await Query.Order.Select((_, index) => new {_.Id}).ToListAsync();
                var chunked = Query.Order.Chunk(10);
                var zipped = Query.Order.Zip(Query.Order);
                var folded = Query.Order.Aggregate((left, right) => left);
                """));

    [Test]
    public Task Cast() =>
        Verify(Analyze("await Query.Asset.Cast<VehicleQueryModel>().ToListAsync();"));

    [Test]
    public Task SelectManyWithResultSelector() =>
        Verify(
            Analyze("await Query.Order.SelectMany(_ => _.Lines, (order, line) => new {order.Id, line.Price}).ToListAsync();"));

    [Test]
    public Task ComparerOverloads() =>
        Verify(
            Analyze(
                """
                await Query.Order.Distinct(null!).Select(_ => new {_.Id}).ToListAsync();
                await Query.Order.OrderBy(_ => _.Region, null!).ToListAsync();
                await Query.Order.Select(_ => new {_.Id}).Union(Query.Order.Select(_ => new {_.Id}), null!).ToListAsync();
                await Query.Order.GroupBy(_ => _.Region, EqualityComparer<string>.Default).Select(_ => new {_.Key}).ToListAsync();
                """));

    // The composition rules QueryValidator enforces server-side. A second Select today costs a round
    // trip and a 400 rather than a translation failure, which is the case the analyzer improves most.
    [Test]
    public Task SingleUseOperators() =>
        Verify(
            Analyze(
                """
                await Query.Order.Select(_ => new {_.Id, _.Region}).Select(_ => new {_.Region}).ToListAsync();
                await Query.Order.Select(_ => new {_.Id}).Distinct().Distinct().ToListAsync();
                await Query.Order.SelectMany(_ => _.Lines).SelectMany(_ => _.Parts).ToListAsync();
                """));

    [Test]
    public Task OrderingKey() =>
        Verify(
            Analyze(
                """
                await Query.Order.OrderBy(_ => new {_.Region, _.Amount}).ToListAsync();
                await Query.Order.OrderBy(_ => _.Region).ThenByDescending(_ => new {_.Amount}).ToListAsync();
                """));

    [Test]
    public Task Projection() =>
        Verify(
            Analyze(
                """
                await Query.Order.Select(_ => _.Region).ToListAsync();
                await Query.Order.GroupBy(_ => _.Region).Select(_ => _.Key).ToListAsync();
                """));

    [Test]
    public Task UnsupportedFunctions() =>
        Verify(
            Analyze(
                """
                await Query.Order.Where(_ => _.Region.PadLeft(5) == "x").ToListAsync();
                await Query.Order.Where(_ => _.Region.Equals("x")).ToListAsync();
                await Query.Order.Where(_ => _.Placed.Ticks > 0).ToListAsync();
                await Query.Order.Where(_ => _.Placed.TimeOfDay.Hours > 0).ToListAsync();
                await Query.Order.Where(_ => Math.Cbrt(_.Rate) > 1).ToListAsync();
                await Query.Order.Where(_ => _.Region.Trim('x') == "y").ToListAsync();
                """));

    [Test]
    public Task FormattedText() =>
        Verify(
            Analyze(
                """
                await Query.Order.Select(_ => new {Amount = _.Amount.ToString("N2")}).ToListAsync();
                await Query.Order.Select(_ => new {Label = $"{_.Amount:N2}"}).ToListAsync();
                await Query.Order.Select(_ => new {Label = $"{_.Amount,10}"}).ToListAsync();
                """));

    [Test]
    public Task SynchronousExecution() =>
        Verify(
            Analyze(
                """
                var rows = Query.Order.ToList();
                var one = Query.Order.First();
                var count = Query.Order.Count();
                var any = Query.Order.Where(_ => _.Amount > 0).Any();
                var loose = Query.Order.AsEnumerable();
                """));

    [Test]
    public Task UnorderedReverse() =>
        Verify(
            Analyze(
                """
                await Query.Order.Reverse().ToListAsync();
                await Query.Order.OrderBy(_ => _.Region).Reverse().ToListAsync();
                """));

    [Test]
    public Task ProjectedGroup() =>
        Verify(
            Analyze(
                """
                await Query.Customer
                    .GroupJoin(Query.Order, _ => _.Id, _ => _.CustomerId, (customer, orders) => new {customer.Id, Orders = orders})
                    .ToListAsync();
                """));

    // A call outside the callable surface that still reads the row is not an unsupported overload of
    // something carried — it is code that only exists client-side, with nothing on the wire to carry
    // it. Helpers, Parse, extension methods and delegates all land here.
    [Test]
    public Task ClientSideCode() =>
        Verify(
            Analyze(
                """
                Func<decimal, decimal> tax = _ => _;
                await Query.Order.Where(_ => Munge(_.Region) == "x").ToListAsync();
                await Query.Order.Where(_ => _.Region.Slugify().Length > 0).ToListAsync();
                await Query.Order.Where(_ => int.Parse(_.Region) > 0).ToListAsync();
                await Query.Order.Where(_ => tax(_.Amount) > 10).ToListAsync();
                await Query.Order.Select(_ => new {_.Id, Code = Convert.ToInt32(_.Amount)}).ToListAsync();
                """));

    // Every operator, function and shape the closed set does carry. A false positive here is worse
    // than a missed rule: it reports code that works, in a build the consumer cannot see past.
    [Test]
    public void SupportedQueriesAreClean()
    {
        const string queries =
            """
            await Query.Order
                .Where(_ => _.Region.StartsWith("N") && _.Amount > 10)
                .OrderBy(_ => _.Region)
                .ThenByDescending(_ => _.Amount)
                .Skip(10)
                .Take(20)
                .Select(_ => new {_.Id, Shouted = _.Region.ToUpper(), Net = _.Amount - _.Discount})
                .ToListAsync();

            await Query.Order
                .GroupBy(_ => new {_.Region, _.Grade})
                .Where(_ => _.Count() > 5)
                .Select(_ => new {_.Key.Region, Total = _.Sum(_ => _.Amount), Rows = _.Count()})
                .ToListAsync();

            await Query.Order
                .Where(_ => _.Placed.Year == 2026 && _.Placed.AddDays(3).Month < 4)
                .Where(_ => Math.Abs(_.Rate) > 1 && Math.Round(_.Rate, 2) < 9)
                .Where(_ => _.Region.Trim().Substring(0, 2).Contains("N"))
                .Where(_ => _.Region.Length > 0 && !string.IsNullOrWhiteSpace(_.Region))
                .Select(_ => new {Label = $"{_.Region} - {_.Amount}", Text = _.Amount.ToString()})
                .ToListAsync();

            await Query.Order.SelectMany(_ => _.Lines).Select(_ => new {_.Price}).ToListAsync();
            await Query.Asset.OfType<VehicleQueryModel>().Select(_ => new {_.Wheels}).ToListAsync();
            await Query.Order.Select(_ => new {_.Id}).Distinct().ToListAsync();
            await Query.Order.OrderBy(_ => _.Amount).Reverse().ToListAsync();

            await Query.Customer
                .Join(Query.Order, _ => _.Id, _ => _.CustomerId, (customer, order) => new {customer.Name, order.Amount})
                .ToListAsync();

            await Query.Customer
                .GroupJoin(Query.Order, _ => _.Id, _ => _.CustomerId, (customer, orders) => new {customer.Name, Total = orders.Sum(_ => _.Amount)})
                .ToListAsync();

            await Query.Order.Select(_ => new {_.Id}).Union(Query.Order.Select(_ => new {_.Id})).ToListAsync();

            var ids = new List<int> {1, 2};
            var regions = new[] {"north", "south"};
            await Query.Order.Where(_ => ids.Contains(_.Id) && regions.Contains(_.Region)).ToListAsync();

            await Query.Order.Where(_ => _.Amount > 0).SumAsync(_ => _.Amount);
            await Query.Order.FirstAsync(_ => _.Region == "N");
            await Query.Order.CountAsync();
            await Query.Order.Select(_ => new {_.Id, _.Region}).ToDictionaryAsync(_ => _.Id);
            """;

        Assert.That(Analyze(queries), Is.Empty);
    }

    // A value that does not come off the row is closure state: the translator evaluates it into a
    // constant before the query is sent, so what was called on it is beside the point.
    [Test]
    public void ClosureStateIsNotAFunctionCall()
    {
        const string queries =
            """
            var prefix = "north";
            var cutoff = DateTime.Now.AddDays(-7).ToString("O");
            await Query.Order.Where(_ => _.Region == prefix.PadLeft(9).Normalize()).ToListAsync();
            await Query.Order.Where(_ => _.Region == cutoff).ToListAsync();
            await Query.Order.Where(_ => _.Amount > decimal.Parse(prefix)).ToListAsync();
            """;

        Assert.That(Analyze(queries), Is.Empty);
    }

    // A chain inside a query lambda reads a row rather than composing the query, and answers to a
    // different rule set — a Select of one value is required there, and Contains is a SQL IN rather
    // than a synchronous terminal.
    [Test]
    public void ChainsInsideQueryLambdasAreLeftToTheTranslator()
    {
        const string queries =
            """
            await Query.Order
                .Where(_ => Query.Customer.Select(c => c.Name).Contains(_.Region))
                .Where(_ => _.Lines.Any(l => l.Price > 3) && _.Lines.Count() > 1)
                .Select(_ => new {_.Id, Lines = _.Lines.Count()})
                .ToListAsync();
            """;

        Assert.That(Analyze(queries), Is.Empty);
    }

    // The element type stops being a query model the moment a Select projects an anonymous type, so
    // the chain is recognised by what it bottoms out at rather than by what it currently carries.
    [Test]
    public Task ChainsThroughLocalsAreStillRecognised() =>
        Verify(
            Analyze(
                """
                var orders = Query.Order.Where(_ => _.Amount > 0);
                var projected = orders.Select(_ => new {_.Id, _.Region});
                await projected.Select(_ => new {_.Region}).ToListAsync();
                """));

    // A hand-built source carries no [ScryModel] — it is opened through the client by name — so it is
    // recognised by the call that opens it instead, and held to the same set.
    [Test]
    public Task HandBuiltSourcesAreRecognised() =>
        Verify(
            Analyze(
                """
                await client.Source<HandBuilt>("Order").Cast<HandBuilt>().ToListAsync();
                await client.Source<HandBuilt>("Order").Select(_ => _.Name).ToListAsync();
                """));

    // Ordinary LINQ over an ordinary collection is not Scry's business.
    [Test]
    public void NonScryQueriesAreIgnored()
    {
        const string queries =
            """
            var numbers = new[] {1, 2, 3}.AsQueryable();
            var taken = numbers.Cast<object>().SkipWhile(_ => true).ToList();
            var text = numbers.Select(_ => _.ToString("N2")).Reverse().ToList();
            """;

        Assert.That(Analyze(queries), Is.Empty);
    }

    static string Analyze(string queries)
    {
        var compilation = CSharpCompilation.Create(
            "Consumer",
            [CSharpSyntaxTree.ParseText(Wrap(queries), path: "Queries.cs")],
            References(),
            new(OutputKind.DynamicallyLinkedLibrary));

        // Only the analyzer's own rules are asserted on. The snippets are deliberately loose — null!
        // sources for comparer overloads, unawaited tasks — and the compiler's opinion of that is
        // beside the point.
        var errors = compilation.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.That(errors, Is.Empty, () => string.Join("\n", errors));

        var diagnostics = compilation
            .WithAnalyzers([new ScryLinqAnalyzer()])
            .GetAnalyzerDiagnosticsAsync()
            .GetAwaiter()
            .GetResult();

        return string.Join(
            Environment.NewLine,
            diagnostics
                .OrderBy(_ => _.Location.SourceSpan.Start)
                .Select(Describe));
    }

    static string Describe(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.GetLineSpan();
        var line = diagnostic.Location.SourceTree!
            .GetText()
            .Lines[span.StartLinePosition.Line]
            .ToString()
            .Trim();
        return $"{diagnostic.Id}: {diagnostic.GetMessage()}{Environment.NewLine}    {line}";
    }

    static string Wrap(string queries) =>
        $$"""
        using System;
        using System.Collections.Generic;
        using System.Linq;
        using System.Linq.Expressions;
        using System.Threading.Tasks;
        using Scry;
        using Scry.Generated;

        namespace Scry
        {
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class ScryModelAttribute(string source, params string[] members) : Attribute
            {
                public string Source { get; } = source;
                public IReadOnlyList<string> Members { get; } = members;
            }

            public class ScryClient
            {
                public IQueryable<T> Source<T>(string name, string[]? members = null) => null!;
            }

            public static class ScryQueryableExtensions
            {
                public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source) => null!;
                public static Task<T[]> ToArrayAsync<T>(this IQueryable<T> source) => null!;
                public static Task<T> FirstAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate) => null!;
                public static Task<bool> AnyAsync<T>(this IQueryable<T> source) => null!;
                public static Task<int> CountAsync<T>(this IQueryable<T> source) => null!;
                public static Task<decimal> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector) => null!;
                public static Task<Dictionary<TKey, T>> ToDictionaryAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector) => null!;
            }

            public static class ScryBatchExtensions
            {
                public static IQueryable<T> Enrol<T>(this IQueryable<T> source) => source;
            }
        }

        namespace Scry.Generated
        {
            [ScryModel("Order", "Id", "Region", "Amount")]
            public class OrderQueryModel
            {
                public int Id { get; init; }
                public int CustomerId { get; init; }
                public string Region { get; init; } = "";
                public string Grade { get; init; } = "";
                public decimal Amount { get; init; }
                public decimal Discount { get; init; }
                public double Rate { get; init; }
                public DateTime Placed { get; init; }
                public List<OrderLineQueryModel> Lines { get; init; } = [];
            }

            [ScryModel("OrderLine", "Id", "Price")]
            public class OrderLineQueryModel
            {
                public int Id { get; init; }
                public decimal Price { get; init; }
                public List<OrderLineQueryModel> Parts { get; init; } = [];
            }

            [ScryModel("Customer", "Id", "Name")]
            public class CustomerQueryModel
            {
                public int Id { get; init; }
                public string Name { get; init; } = "";
            }

            [ScryModel("Asset", "Id")]
            public class AssetQueryModel
            {
                public int Id { get; init; }
            }

            [ScryModel("Vehicle", "Id", "Wheels")]
            public class VehicleQueryModel : AssetQueryModel
            {
                public int Wheels { get; init; }
            }

            public sealed class ScryQuery
            {
                public IQueryable<OrderQueryModel> Order => null!;
                public IQueryable<CustomerQueryModel> Customer => null!;
                public IQueryable<AssetQueryModel> Asset => null!;
            }
        }

        // A source the generator never saw: opened through the client by name, with no attribute to
        // recognise it by.
        public class HandBuilt
        {
            public int Id { get; init; }
            public string Name { get; init; } = "";
        }

        // Client-side code a query might mistakenly reach for.
        public static class ClientSideHelpers
        {
            public static string Slugify(this string value) => value;
        }

        public class Queries
        {
            readonly ScryQuery Query = null!;
            readonly ScryClient client = null!;

            static string Munge(string value) => value;

            public async Task Run()
            {
        {{queries}}
            }
        }
        """;

    static List<MetadataReference> References()
    {
        var trusted = (string) AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return trusted
            .Split(Path.PathSeparator)
            .Where(_ => _.Length > 0)
            .Select(MetadataReference (_) => MetadataReference.CreateFromFile(_))
            .ToList();
    }
}
