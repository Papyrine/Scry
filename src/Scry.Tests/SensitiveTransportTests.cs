/// <summary>
/// How a query touching a <c>[Sensitive]</c> member travels. The rule is about constants, not mentions:
/// what leaks from a URL is the value written into it, so comparing a marked member against one sends
/// the query as a body, while ordering by that member or returning it leaves the transport alone.
/// </summary>
/// <remarks>
/// Driven through the real client against a stub, because the method chosen is the whole behaviour —
/// and asserted on shapes the translator builds four different ways, since a check anywhere but the
/// finished request would answer for some of them and silently miss the rest.
/// </remarks>
[TestFixture]
public class SensitiveTransportTests
{
    [ScryModel("Person", "Id", "Name", "Ssn")]
    public class PersonModel
    {
        public int Id { get; init; }
        public string Name { get; init; } = "";

        [ScrySensitive]
        public string Ssn { get; init; } = "";

        public HomeModel? Home { get; init; }
    }

    [ScrySensitive]
    public class HomeModel
    {
        public string City { get; init; } = "";
    }

    public record NameRow(string Name);

    public class UnmarkedPerson
    {
        public int Id { get; init; }
        public string Ssn { get; init; } = "";
    }

    // Two clients in one process opening the same source name as different models: a generated one,
    // and a hand-built one that marks nothing. The registry once kept whichever registered last, so
    // the unmarked model answered for the marked one's query and its constant went out in a URL.
    [Test]
    public void ALaterUnmarkedModelDoesNotUnmarkASource()
    {
        var marked = new ScryClient((_, _) => throw new("not sent"));
        var request = marked.Source<PersonModel>("Citizen")
            .Where(_ => _.Ssn == "123-45-6789")
            .Select(_ => new NameRow(_.Name))
            .ToScryRequest();

        var other = new ScryClient((_, _) => throw new("not sent"));
        _ = other.Source<UnmarkedPerson>("Citizen");

        Assert.That(ScryClient.RequiresBody(request), Is.True);
    }

    [Test]
    public async Task ConstantComparedAgainstAMarkedMemberTravelsAsABody()
    {
        var method = await Method(
            _ => _.Where(_ => _.Ssn == "123-45-6789")
                .Select(_ => new NameRow(_.Name))
                .ToListAsync());

        Assert.That(method, Is.EqualTo(HttpMethod.Post));
    }

    // The shape a check in the translator misses: a terminal's predicate is translated by a throwaway
    // translator, so nothing it learned could reach the transport.
    [Test]
    public async Task ConstantInATerminalPredicateTravelsAsABody()
    {
        var method = await Method(_ => _.CountAsync(_ => _.Ssn == "123-45-6789"));

        Assert.That(method, Is.EqualTo(HttpMethod.Post));
    }

    [Test]
    public async Task ConstantComparedAgainstAMarkedTypesMemberTravelsAsABody()
    {
        var method = await Method(
            _ => _.Where(_ => _.Home!.City == "Hobart")
                .Select(_ => new NameRow(_.Name))
                .ToListAsync());

        Assert.That(method, Is.EqualTo(HttpMethod.Post));
    }

    // Returning the member puts nothing in the URL. What it does put on the caller's disk is the
    // server's to refuse, with no-store — see the server's own tests.
    [Test]
    public async Task ProjectingAMarkedMemberKeepsTheUrl()
    {
        var method = await Method(
            _ => _.Where(_ => _.Id == 42)
                .Select(_ => new SsnRow(_.Ssn))
                .ToListAsync());

        Assert.That(method, Is.EqualTo(HttpMethod.Get));
    }

    [Test]
    public async Task OrderingByAMarkedMemberKeepsTheUrl()
    {
        var method = await Method(
            _ => _.OrderBy(_ => _.Ssn)
                .Select(_ => new NameRow(_.Name))
                .ToListAsync());

        Assert.That(method, Is.EqualTo(HttpMethod.Get));
    }

    // A page ordered by a marked member keeps the URL too: its cursor carries the last row's value
    // of that member, and the cursor is sealed, so nothing of the value reaches the URL of the next
    // page — see CursorCodecTests.DoesNotCarryTheKeyValuesInTheClear.
    [Test]
    public async Task PagingByAMarkedMemberKeepsTheUrl()
    {
        var method = await Method(
            _ => _.OrderBy(_ => _.Ssn)
                .Select(_ => new NameRow(_.Name))
                .ToPageAsync(10));

        Assert.That(method, Is.EqualTo(HttpMethod.Get));
    }

    // A constant somewhere in a query that also names a marked member elsewhere is treated as though
    // the two met. Deliberately blunt: the shapes it is not exact for all err toward the body.
    [Test]
    public async Task ConstantElsewhereInTheSameFilterTravelsAsABody()
    {
        var method = await Method(
            _ => _.Where(_ => _.Name == "Ada" && _.Ssn != "")
                .Select(_ => new NameRow(_.Name))
                .ToListAsync());

        Assert.That(method, Is.EqualTo(HttpMethod.Post));
    }

    [Test]
    public async Task AQueryTouchingNothingMarkedKeepsTheUrl()
    {
        var method = await Method(
            _ => _.Where(_ => _.Name == "Ada")
                .Select(_ => new NameRow(_.Name))
                .ToListAsync());

        Assert.That(method, Is.EqualTo(HttpMethod.Get));
    }

    public record SsnRow(string Ssn);

    static async Task<HttpMethod?> Method(Func<IQueryable<PersonModel>, Task> query)
    {
        HttpMethod? method = null;
        var http = new HttpClient(
            new StubHandler(
                request =>
                {
                    method = request.Method;
                    return new(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            ScryJson.Serialize(
                                QueryResponse.Create(ResultKind.Scalar, JsonSerializer.SerializeToElement(0))))
                    };
                }))
        {
            BaseAddress = new("http://localhost")
        };

        var client = ScryClient.ForHttp(http, "/api/query");
        try
        {
            await query(client.Source<PersonModel>("Person", ["Id", "Name", "Ssn"]));
        }
        catch (Exception)
        {
            // The stub answers every query with a scalar, which a list terminal cannot read. The method
            // was chosen before the response was written, which is the whole of what is asserted here.
        }

        return method;
    }

    sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, Cancel cancel) =>
            Task.FromResult(respond(request));
    }
}
