[TestFixture]
public class AnalyzerTests
{
    [Test]
    public Task UnsupportedOperators() =>
        Verify(
            Analyze(
                """
                await Query.Order.GroupBy(_ => _.Region, _ => _.Amount).Select(_ => new {Total = _.Sum(v => v)}).ToListAsync();
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
        Verify(Analyze("await Query.Asset.Cast<VehicleQueryModel>().ToListAsync();"))
            .Snapshot(
                """
                SCRY101: Cast is not supported by Scry — use OfType<VehicleQueryModel> to narrow by filtering
                    await Query.Asset.Cast<VehicleQueryModel>().ToListAsync();
                """);

    [Test]
    public Task SelectManyWithResultSelector() =>
        Verify(
            Analyze("await Query.Order.SelectMany(_ => _.Lines, (order, line) => new {order.Id, line.Price}).ToListAsync();"))
            .Snapshot(
                """
                SCRY102: SelectMany with a result selector is not supported by Scry — flatten first, then Select
                    await Query.Order.SelectMany(_ => _.Lines, (order, line) => new {order.Id, line.Price}).ToListAsync();
                """);

    [Test]
    public Task ComparerOverloads() =>
        Verify(
            Analyze(
                """
                await Query.Order.Distinct(null!).Select(_ => new {_.Id}).ToListAsync();
                await Query.Order.OrderBy(_ => _.Region, null!).ToListAsync();
                await Query.Order.Select(_ => new {_.Id}).Union(Query.Order.Select(_ => new {_.Id}), null!).ToListAsync();
                await Query.Order.GroupBy(_ => _.Region, EqualityComparer<string>.Default).Select(_ => new {_.Key}).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY103: 'Distinct' with a comparer is not supported by Scry — the comparison happens in the database, which cannot run a client-side comparer
                        await Query.Order.Distinct(null!).Select(_ => new {_.Id}).ToListAsync();
                    SCRY103: 'OrderBy' with a comparer is not supported by Scry — the comparison happens in the database, which cannot run a client-side comparer
                        await Query.Order.OrderBy(_ => _.Region, null!).ToListAsync();
                    SCRY103: 'Union' with a comparer is not supported by Scry — the comparison happens in the database, which cannot run a client-side comparer
                        await Query.Order.Select(_ => new {_.Id}).Union(Query.Order.Select(_ => new {_.Id}), null!).ToListAsync();
                    SCRY103: 'GroupBy' with a comparer is not supported by Scry — the comparison happens in the database, which cannot run a client-side comparer
                        await Query.Order.GroupBy(_ => _.Region, EqualityComparer<string>.Default).Select(_ => new {_.Key}).ToListAsync();
                    """);

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
                await Query.Order.GroupBy(_ => _.Region, (region, orders) => new {Region = region}).Select(_ => new {_.Region}).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY104: A Scry query may carry only one Select; this is the second, and the server rejects the request
                        await Query.Order.Select(_ => new {_.Id, _.Region}).Select(_ => new {_.Region}).ToListAsync();
                    SCRY104: A Scry query may carry only one Distinct; this is the second, and the server rejects the request
                        await Query.Order.Select(_ => new {_.Id}).Distinct().Distinct().ToListAsync();
                    SCRY104: A Scry query may carry only one SelectMany; this is the second, and the server rejects the request
                        await Query.Order.SelectMany(_ => _.Lines).SelectMany(_ => _.Parts).ToListAsync();
                    SCRY104: A Scry query may carry only one Select; this is the second, and the server rejects the request
                        await Query.Order.GroupBy(_ => _.Region, (region, orders) => new {Region = region}).Select(_ => new {_.Region}).ToListAsync();
                    """);

    [Test]
    public Task OrderingKey() =>
        Verify(
            Analyze(
                """
                await Query.Order.OrderBy(_ => new {_.Region, _.Amount}).ToListAsync();
                await Query.Order.OrderBy(_ => _.Region).ThenByDescending(_ => new {_.Amount}).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY105: 'OrderBy' takes a single value as its key — a constructed object has no ordering of its own
                        await Query.Order.OrderBy(_ => new {_.Region, _.Amount}).ToListAsync();
                    SCRY105: 'ThenByDescending' takes a single value as its key — a constructed object has no ordering of its own
                        await Query.Order.OrderBy(_ => _.Region).ThenByDescending(_ => new {_.Amount}).ToListAsync();
                    """);

    [Test]
    public Task Projection() =>
        Verify(
            Analyze(
                """
                await Query.Order.Select(_ => _.Region).ToListAsync();
                await Query.Order.GroupBy(_ => _.Region).Select(_ => _.Key).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY106: A Scry projection must construct an object — an anonymous type, a record, or an object initializer
                        await Query.Order.Select(_ => _.Region).ToListAsync();
                    SCRY106: A Scry projection must construct an object — an anonymous type, a record, or an object initializer
                        await Query.Order.GroupBy(_ => _.Region).Select(_ => _.Key).ToListAsync();
                    """);

    [Test]
    public Task UnsupportedFunctions() =>
        Verify(
            Analyze(
                """
                await Query.Order.Where(_ => _.Region.PadLeft(5) == "x").ToListAsync();
                await Query.Order.Where(_ => _.Placed.Ticks > 0).ToListAsync();
                await Query.Order.Where(_ => _.Placed.TimeOfDay.TotalHours > 0).ToListAsync();
                await Query.Order.Where(_ => Math.Cbrt(_.Rate) > 1).ToListAsync();
                await Query.Order.Where(_ => _.Region.Trim('x') == "y").ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY107: 'String.PadLeft' is not one of the functions Scry can carry
                        await Query.Order.Where(_ => _.Region.PadLeft(5) == "x").ToListAsync();
                    SCRY107: 'DateTime.Ticks' is not one of the functions Scry can carry
                        await Query.Order.Where(_ => _.Placed.Ticks > 0).ToListAsync();
                    SCRY107: 'TimeSpan.TotalHours' is not one of the functions Scry can carry
                        await Query.Order.Where(_ => _.Placed.TimeOfDay.TotalHours > 0).ToListAsync();
                    SCRY107: 'Math.Cbrt' is not one of the functions Scry can carry
                        await Query.Order.Where(_ => Math.Cbrt(_.Rate) > 1).ToListAsync();
                    SCRY107: 'this overload of String.Trim' is not one of the functions Scry can carry
                        await Query.Order.Where(_ => _.Region.Trim('x') == "y").ToListAsync();
                    """);

    [Test]
    public Task FormattedText() =>
        Verify(
            Analyze(
                """
                await Query.Order.Select(_ => new {Amount = _.Amount.ToString("N2")}).ToListAsync();
                await Query.Order.Select(_ => new {Label = $"{_.Amount:N2}"}).ToListAsync();
                await Query.Order.Select(_ => new {Label = $"{_.Amount,10}"}).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY108: ToString with a format is not supported by Scry — format the value after the query returns
                        await Query.Order.Select(_ => new {Amount = _.Amount.ToString("N2")}).ToListAsync();
                    SCRY108: ToString with a format is not supported by Scry — format the value after the query returns
                        await Query.Order.Select(_ => new {Label = $"{_.Amount:N2}"}).ToListAsync();
                    SCRY108: ToString with a format is not supported by Scry — format the value after the query returns
                        await Query.Order.Select(_ => new {Label = $"{_.Amount,10}"}).ToListAsync();
                    """);

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
                var top = Query.Order.MaxBy(_ => _.Amount);
                """));

    // foreach runs the query without calling a terminal, so it is caught by what it enumerates rather
    // than by a chain walk — a bare source, a composed one, and one held in a local alike.
    [Test]
    public Task SynchronousEnumeration() =>
        Verify(
            Analyze(
                """
                foreach (var order in Query.Order)
                {
                }

                foreach (var order in Query.Order.Where(_ => _.Amount > 0))
                {
                }

                var projected = Query.Order.Select(_ => new {_.Id});
                foreach (var order in projected)
                {
                }
                """))
                .Snapshot(
                    """
                    SCRY109: 'foreach' executes the query where it stands, which a Scry source cannot do — await ToListAsync and iterate what it returns
                        foreach (var order in Query.Order)
                    SCRY109: 'foreach' executes the query where it stands, which a Scry source cannot do — await ToListAsync and iterate what it returns
                        foreach (var order in Query.Order.Where(_ => _.Amount > 0))
                    SCRY109: 'foreach' executes the query where it stands, which a Scry source cannot do — await ToListAsync and iterate what it returns
                        foreach (var order in projected)
                    """);

    // The streaming idiom, and the one enumeration that is not a mistake: an await foreach reads what a
    // ToAsyncEnumerable terminal returned rather than the provider. A captured query has no
    // GetAsyncEnumerator of its own, so nothing else can be written this way — and reporting it would
    // advise buffering the whole result, which is what streaming exists to avoid.
    [Test]
    public void AsynchronousEnumerationIsClean()
    {
        const string queries =
            """
            await foreach (var order in Query.Order.Where(_ => _.Amount > 0).ToAsyncEnumerable())
            {
            }

            var streamed = Query.Order.Select(_ => new {_.Id}).ToAsyncEnumerable();
            await foreach (var order in streamed)
            {
            }
            """;

        Assert.That(Analyze(queries), Is.Empty);
    }

    [Test]
    public Task UnorderedReverse() =>
        Verify(
            Analyze(
                """
                await Query.Order.Reverse().ToListAsync();
                await Query.Order.OrderBy(_ => _.Region).Reverse().ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY110: Reverse requires a preceding OrderBy, as EF does
                        await Query.Order.Reverse().ToListAsync();
                    """);

    [Test]
    public Task ProjectedGroup() =>
        Verify(
            Analyze(
                """
                await Query.Customer
                    .GroupJoin(Query.Order, _ => _.Id, _ => _.CustomerId, (customer, orders) => new {customer.Id, Orders = orders})
                    .ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY111: A GroupJoin's group can only be folded to a scalar — 'orders' would put a nested collection in the response
                        .GroupJoin(Query.Order, _ => _.Id, _ => _.CustomerId, (customer, orders) => new {customer.Id, Orders = orders})
                    """);

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
                await Query.Order.Where(_ => char.IsDigit(_.Region, 0)).ToListAsync();
                await Query.Order.Where(_ => tax(_.Amount) > 10).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY112: 'Queries.Munge' is client-side code, which a Scry query cannot carry — evaluate it before the query, or apply it to the rows after they return
                        await Query.Order.Where(_ => Munge(_.Region) == "x").ToListAsync();
                    SCRY112: 'ClientSideHelpers.Slugify' is client-side code, which a Scry query cannot carry — evaluate it before the query, or apply it to the rows after they return
                        await Query.Order.Where(_ => _.Region.Slugify().Length > 0).ToListAsync();
                    SCRY112: 'Char.IsDigit' is client-side code, which a Scry query cannot carry — evaluate it before the query, or apply it to the rows after they return
                        await Query.Order.Where(_ => char.IsDigit(_.Region, 0)).ToListAsync();
                    SCRY112: 'Func.Invoke' is client-side code, which a Scry query cannot carry — evaluate it before the query, or apply it to the rows after they return
                        await Query.Order.Where(_ => tax(_.Amount) > 10).ToListAsync();
                    """);

    // Every operator, function and shape the closed set does carry. A false positive here is worse
    // than a missed rule: it reports code that works, in a build the consumer cannot see past.
    // An attachment is fetched by its row's key, so a projection carrying one has to carry the key
    // too. Reported as an error rather than a warning: the handle could not be built at all.
    [Test]
    public Task AttachmentWithoutItsKey() =>
        Verify(
            Analyze(
                """
                await Query.Contract.Select(_ => new {_.Name, _.Document}).ToListAsync();
                await Query.Contract.Select(_ => new {_.Name, Parent = new {_.Parent!.Name, _.Parent!.Document}}).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY113: Attachment 'Document' needs '_.Id' projected beside it — an attachment is fetched by its row's key, so the key has to come back with the row
                        await Query.Contract.Select(_ => new {_.Name, _.Document}).ToListAsync();
                    SCRY113: Attachment 'Document' needs '_.Parent.Id' projected beside it — an attachment is fetched by its row's key, so the key has to come back with the row
                        await Query.Contract.Select(_ => new {_.Name, Parent = new {_.Parent!.Name, _.Parent!.Document}}).ToListAsync();
                    """);

    [Test]
    public Task AttachmentUsedAsValue() =>
        Verify(
            Analyze(
                """
                await Query.Contract.Where(_ => _.Document != null).ToListAsync();
                await Query.Contract.OrderBy(_ => _.Document).ToListAsync();
                await Query.Contract.GroupBy(_ => _.Document).Select(_ => new {Rows = _.Count()}).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY114: Attachment 'Document' is not a value, so it cannot be filtered, ordered, grouped, or computed on
                        await Query.Contract.Where(_ => _.Document != null).ToListAsync();
                    SCRY114: Attachment 'Document' is not a value, so it cannot be filtered, ordered, grouped, or computed on
                        await Query.Contract.OrderBy(_ => _.Document).ToListAsync();
                    SCRY115: An attachment cannot be carried through 'GroupBy' — the result's rows no longer correspond to single rows of the source it is fetched from
                        await Query.Contract.GroupBy(_ => _.Document).Select(_ => new {Rows = _.Count()}).ToListAsync();
                    SCRY114: Attachment 'Document' is not a value, so it cannot be filtered, ordered, grouped, or computed on
                        await Query.Contract.GroupBy(_ => _.Document).Select(_ => new {Rows = _.Count()}).ToListAsync();
                    """);

    [Test]
    public Task AttachmentUnderRewritingOperator() =>
        Verify(
            Analyze(
                """
                await Query.Contract.Distinct().ToListAsync();
                await Query.Contract.Select(_ => new {_.Id, _.Document}).Distinct().ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY115: An attachment cannot be carried through 'Distinct' — the result's rows no longer correspond to single rows of the source it is fetched from
                        await Query.Contract.Distinct().ToListAsync();
                    SCRY115: An attachment cannot be carried through 'Distinct' — the result's rows no longer correspond to single rows of the source it is fetched from
                        await Query.Contract.Select(_ => new {_.Id, _.Document}).Distinct().ToListAsync();
                    """);

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
                .GroupBy(_ => _.Region)
                .Where(_ => _.Where(_ => _.Amount > 5).Count() > 1)
                .Select(_ => new {_.Key, Big = _.Count(_ => _.Amount > 9), Grades = _.Select(x => x.Grade).Distinct().Count(), Codes = string.Concat(_.Select(x => x.Grade))})
                .ToListAsync();

            await Query.Order
                .Where(_ => _.Placed.Year == 2026 && _.Placed.AddDays(3).Month < 4)
                .Where(_ => Math.Abs(_.Rate) > 1 && Math.Round(_.Rate, 2) < 9)
                .Where(_ => _.Region.Trim().Substring(0, 2).Contains("N"))
                .Where(_ => _.Region.Length > 0 && !string.IsNullOrWhiteSpace(_.Region))
                .Select(_ => new {Label = $"{_.Region} - {_.Amount}", Text = _.Amount.ToString()})
                .ToListAsync();

            await Query.Order
                .Where(_ => _.Region.Equals("N") || string.Equals(_.Region, "S"))
                .Where(_ => _.Grade.Equals('A') && _.Rebate.HasValue && _.Rebate.Value > 1)
                .Where(_ => _.Placed.TimeOfDay.Hours > 0 && _.Placed.Microsecond == 0)
                .ToListAsync();

            await Query.Order.SelectMany(_ => _.Lines).Select(_ => new {_.Price}).ToListAsync();
            await Query.Asset.OfType<VehicleQueryModel>().Select(_ => new {_.Wheels}).ToListAsync();
            await Query.Order.Select(_ => new {_.Id}).Distinct().ToListAsync();
            await Query.Order.OrderBy(_ => _.Amount).Reverse().ToListAsync();

            await Query.Customer
                .Join(Query.Order, _ => _.Id, _ => _.CustomerId, (customer, order) => new {customer.Name, order.Amount})
                .ToListAsync();

            await Query.Customer
                .Join(Query.Order, _ => new {Key = _.Id, _.Name}, _ => new {Key = _.CustomerId, Name = _.Region}, (customer, order) => new {customer.Name, order.Amount})
                .ToListAsync();

            await Query.Customer
                .GroupJoin(Query.Order, _ => _.Id, _ => _.CustomerId, (customer, orders) => new {customer.Name, Total = orders.Sum(_ => _.Amount)})
                .ToListAsync();

            await Query.Order.Select(_ => new {_.Id}).Union(Query.Order.Select(_ => new {_.Id})).ToListAsync();
            await Query.Order.Select(_ => new {_.Id}).Union(Query.Order.OrderBy(_ => _.Amount).Take(2).Select(_ => new {_.Id})).ToListAsync();
            await Query.Customer
                .Join(Query.Order.OrderByDescending(_ => _.Amount).Take(5), _ => _.Id, _ => _.CustomerId, (customer, order) => new {customer.Name, order.Amount})
                .ToListAsync();

            var ids = new List<int> {1, 2};
            var regions = new[] {"north", "south"};
            await Query.Order.Where(_ => ids.Contains(_.Id) && regions.Contains(_.Region)).ToListAsync();

            await Query.Order.GroupBy(_ => _.Region, (region, orders) => new {Region = region, Total = orders.Sum(_ => _.Amount)}).ToListAsync();
            await Query.Order.GroupBy(_ => _.Grade).Select(_ => new {Grade = _.Key, Regions = string.Join(",", _.Select(_ => _.Region))}).ToListAsync();
            await Query.Order.Where(_ => _.Rebate.GetValueOrDefault() > 0 && _.Rebate.GetValueOrDefault(5m) < 9).ToListAsync();
            await Query.Order.Where(_ => _.Region.StartsWith('N') && _.Region.Replace('o', '0').Length > 0).ToListAsync();
            await Query.Order.Where(_ => _.Placed.AddMilliseconds(250).Year == 2026).ToListAsync();
            await Query.Order.Where(_ => _.Options.HasFlag(OrderFlags.Rush | OrderFlags.Gift)).ToListAsync();
            await Query.Order.Where(_ => int.Parse(_.Region) > 0 && decimal.Parse(_.Region) < 100).ToListAsync();
            await Query.Order.Where(_ => bool.Parse(_.Region) && byte.Parse(_.Region) > 0 && short.Parse(_.Region) > 0 && float.Parse(_.Region) > 1f).ToListAsync();
            await Query.Order.Where(_ => Convert.ToBoolean(_.Region) && Convert.ToByte(_.Region) > 0 && Convert.ToInt16(_.Region) < 9).ToListAsync();
            await Query.Order.Where(_ => Math.Max(_.Amount, _.Discount) > 5 && Math.Min(_.Rate, 1d) < 2).ToListAsync();
            await Query.Order.Where(_ => _.Amount.CompareTo(5m) > 0 && _.Region.CompareTo("x") < 0).ToListAsync();
            await Query.Order.Where(_ => double.DegreesToRadians(_.Rate) > 1 && float.RadiansToDegrees((float)_.Rate) < 90).ToListAsync();
            await Query.Order.Select(_ => new {_.Id, Cmp = string.Compare(_.Region, "x"), When = _.Placed.CompareTo(DateTime.MinValue)}).ToListAsync();
            await Query.Order.Select(_ => new {_.Id, Value = Convert.ToInt64(_.Region), Text = Convert.ToString(_.Amount)}).ToListAsync();

            await Query.Order.Where(_ => _.Amount > 0).SumAsync(_ => _.Amount);
            await Query.Order.FirstAsync(_ => _.Region == "N");
            await Query.Order.CountAsync();
            await Query.Order.Select(_ => new {_.Id, _.Region}).ToDictionaryAsync(_ => _.Id);

            await Query.Contract.ToListAsync();
            await Query.Contract.Where(_ => _.Name == "x").OrderBy(_ => _.Id).ToListAsync();
            await Query.Contract.Select(_ => new {_.Id, _.Document}).ToListAsync();
            await Query.Contract.Select(_ => new {_.Name, Parent = new {_.Parent!.Id, _.Parent!.Document}}).ToListAsync();
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
                """))
                .Snapshot(
                    """
                    SCRY104: A Scry query may carry only one Select; this is the second, and the server rejects the request
                        await projected.Select(_ => new {_.Region}).ToListAsync();
                    """);

    // A hand-built source carries no [ScryModel] — it is opened through the client by name — so it is
    // recognised by the call that opens it instead, and held to the same set.
    [Test]
    public Task HandBuiltSourcesAreRecognised() =>
        Verify(
            Analyze(
                """
                await client.Source<HandBuilt>("Order").Cast<HandBuilt>().ToListAsync();
                await client.Source<HandBuilt>("Order").Select(_ => _.Name).ToListAsync();
                """))
                .Snapshot(
                    """
                    SCRY101: Cast is not supported by Scry — use OfType<HandBuilt> to narrow by filtering
                        await client.Source<HandBuilt>("Order").Cast<HandBuilt>().ToListAsync();
                    SCRY106: A Scry projection must construct an object — an anonymous type, a record, or an object initializer
                        await client.Source<HandBuilt>("Order").Select(_ => _.Name).ToListAsync();
                    """);

    // A Razor component compiles to a tree marked auto-generated, and its @code block is where a
    // Blazor page writes its queries. An analyzer that skipped generated code would be silent there.
    [Test]
    public Task AComponentsGeneratedTreeIsAnalyzed() =>
        Verify(Analyze("await Query.Asset.Cast<VehicleQueryModel>().ToListAsync();", generated: true))
            .Snapshot(
                """
                SCRY101: Cast is not supported by Scry — use OfType<VehicleQueryModel> to narrow by filtering
                    await Query.Asset.Cast<VehicleQueryModel>().ToListAsync();
                """);

    // What the generator itself emits into a generated file: an entry point opening a source, with
    // no chain on it. Reading generated code must not turn that into a report the consumer cannot act on.
    [Test]
    public void TheGeneratedEntryPointReportsNothing() =>
        Assert.That(Analyze("var orders = client.Source<HandBuilt>(\"Order\");", generated: true), Is.Empty);

    // Equals is == spelled as a method, which the translator carries over any operands. Owners the
    // callable set has no functions for — a Guid, an enum, a char, a TimeSpan — once read as
    // client-side code or as a function the set lacks, though the same query ran.
    [Test]
    public void EqualsOverAnyScalarIsClean()
    {
        const string queries =
            """
            await Query.Order.Where(_ => _.Reference.Equals(Guid.Empty)).ToListAsync();
            await Query.Order.Where(_ => _.Options.Equals(OrderFlags.Rush)).ToListAsync();
            await Query.Order.Where(_ => _.Tier.Equals('A')).ToListAsync();
            await Query.Order.Where(_ => _.Lead.Equals(TimeSpan.Zero)).ToListAsync();
            await Query.Order.Where(_ => _.Id.Equals(1) && Equals(_.Amount, 1m)).ToListAsync();
            """;

        Assert.That(Analyze(queries), Is.Empty);
    }

    // Ordinary LINQ over an ordinary collection is not Scry's business.
    [Test]
    public void NonScryQueriesAreIgnored()
    {
        const string queries =
            """
            var numbers = new[] {1, 2, 3}.AsQueryable();
            var taken = numbers.Cast<object>().SkipWhile(_ => true).ToList();
            var text = numbers.Select(_ => _.ToString("N2")).Reverse().ToList();
            foreach (var number in numbers)
            {
            }
            """;

        Assert.That(Analyze(queries), Is.Empty);
    }

    // Generated, the file carries the header and the name Razor gives a component's tree.
    static string Analyze(string queries, bool generated = false)
    {
        var tree = generated
            ? CSharpSyntaxTree.ParseText("// <auto-generated/>" + Environment.NewLine + Wrap(queries), path: "Index.razor.g.cs")
            : CSharpSyntaxTree.ParseText(Wrap(queries), path: "Queries.cs");
        var compilation = CSharpCompilation.Create(
            "Consumer",
            [tree],
            References(),
            new(OutputKind.DynamicallyLinkedLibrary));

        // Only the analyzer's own rules are asserted on. The snippets are deliberately loose — null!
        // sources for comparer overloads, unawaited tasks — and the compiler's opinion of that is
        // beside the point.
        var errors = compilation.GetDiagnostics()
            .Where(_ => _.Severity == DiagnosticSeverity.Error)
            .ToList();
        Assert.That(errors, Is.Empty, () => string.Join('\n', errors));

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
                public string[] Keys { get; set; } = [];
                public string[] Attachments { get; set; } = [];
            }

            public sealed class ScryAttachment
            {
                public Task<System.IO.Stream?> OpenAsync() => null!;
            }

            public class ScryClient
            {
                public IQueryable<T> Source<T>(string name, string[]? members = null) => null!;
            }

            public static class ScryQueryableExtensions
            {
                public static Task<List<T>> ToListAsync<T>(this IQueryable<T> source) => null!;
                public static IAsyncEnumerable<T> ToAsyncEnumerable<T>(this IQueryable<T> source) => null!;
                public static Task<T[]> ToArrayAsync<T>(this IQueryable<T> source) => null!;
                public static Task<T> FirstAsync<T>(this IQueryable<T> source, Expression<Func<T, bool>> predicate) => null!;
                public static Task<bool> AnyAsync<T>(this IQueryable<T> source) => null!;
                public static Task<int> CountAsync<T>(this IQueryable<T> source) => null!;
                public static Task<decimal> SumAsync<T>(this IQueryable<T> source, Expression<Func<T, decimal>> selector) => null!;
                public static Task<Dictionary<TKey, T>> ToDictionaryAsync<T, TKey>(this IQueryable<T> source, Func<T, TKey> keySelector) => null!;
                public static Task<T> MaxByAsync<T, TKey>(this IQueryable<T> source, Expression<Func<T, TKey>> keySelector) => null!;
                public static Task<T> MinByAsync<T, TKey>(this IQueryable<T> source, Expression<Func<T, TKey>> keySelector) => null!;
            }

            public static class ScryBatchExtensions
            {
                public static IQueryable<T> Enrol<T>(this IQueryable<T> source) => source;
            }
        }

        namespace Scry.Generated
        {
            [Flags]
            public enum OrderFlags
            {
                None = 0,
                Rush = 1,
                Gift = 2
            }

            [ScryModel("Order", "Id", "Region", "Amount")]
            public class OrderQueryModel
            {
                public int Id { get; init; }
                public int CustomerId { get; init; }
                public string Region { get; init; } = "";
                public string Grade { get; init; } = "";
                public decimal Amount { get; init; }
                public decimal Discount { get; init; }
                public decimal? Rebate { get; init; }
                public OrderFlags Options { get; init; }
                public double Rate { get; init; }
                public DateTime Placed { get; init; }
                public Guid Reference { get; init; }
                public char Tier { get; init; }
                public TimeSpan Lead { get; init; }
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

            [ScryModel("Contract", "Id", "Name", Keys = new[] {"Id"}, Attachments = new[] {"Document"})]
            public class ContractQueryModel
            {
                public int Id { get; init; }
                public string Name { get; init; } = "";
                public ScryAttachment Document { get; init; } = null!;
                public ContractQueryModel? Parent { get; init; }
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
                public IQueryable<ContractQueryModel> Contract => null!;
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
