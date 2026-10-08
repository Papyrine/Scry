/// <summary>
/// The disclosure audit beside conditional requests: a caller told that the copy it holds is still
/// current has been told something, and that is recorded before it is said.
/// </summary>
public partial class DisclosureTests
{
    // What the server needs to answer conditionally at all: a way to tell that nothing changed, and
    // whose cache a response belongs in.
    static void Conditional(ScryOptions options)
    {
        options.QueryFreshness = (_, _) => new("1");
        options.CacheScope = _ => _.Request.Headers["X-User"].ToString();
    }

    // The first answer is recorded as sent, and may be kept: it has to be asked about before it is
    // used again. The asking is answered 304 with nothing run, and recorded as a confirmation of the
    // same request, under whoever asked.
    [Test]
    public async Task BeingToldACopyIsStillCurrentIsRecorded()
    {
        await using var server = await Server.Start(database: null, extra: Conditional);

        using var first = await server.Get(holidays);
        var tag = first.Headers.ETag!.ToString();
        using var second = await server.Get(holidays, tag);

        var events = await server.Store
            .ReceivedBy("tester", DateTimeOffset.MinValue, DateTimeOffset.MaxValue)
            .ToListAsync();
        events.Reverse();

        // The confirmation says nothing was sent, so it is no answer to whether the caller was sent
        // a member: that is the first answer's to give, and it gives it once.
        var sent = await server.Store.MemberReceivedBy("tester", "Holiday", "Name").ToListAsync();
        using (Assert.Multiple())
        {
            await Assert.That(sent).Count().IsEqualTo(1);
            await Assert.That(sent[0].Event.Delivery).IsEqualTo(ScryDisclosureDelivery.Sent);
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(first.Headers.CacheControl!.ToString()).IsEqualTo("no-cache, private");
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That(events).Count().IsEqualTo(2);
            await Assert.That(events[0].Event.Delivery).IsEqualTo(ScryDisclosureDelivery.Sent);
            await Assert.That(events[1].Event.Delivery).IsEqualTo(ScryDisclosureDelivery.Confirmed);
            await Assert.That(events[1].Event.Kind).IsEqualTo(events[0].Event.Kind);
            await Assert.That(events[1].Event.Source).IsEqualTo("Holiday");
            await Assert.That(events[1].Event.Request).IsEqualTo(events[0].Event.Request);
            await Assert.That(events[1].Close!.Units).IsEqualTo(0);
        }
    }

    // A confirmation the store will not take is not given: the caller is not told anything, as with
    // any other answer.
    [Test]
    public async Task ACopyIsNotConfirmedWhereThatCannotBeRecorded()
    {
        var failing = new Failing(after: 1);
        await using var server = await Server.Start(database: null, sink: failing.Over, extra: Conditional);

        using var first = await server.Get(holidays);
        using var second = await server.Get(holidays, first.Headers.ETag!.ToString());

        using (Assert.Multiple())
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.InternalServerError);
            await Assert.That((await server.Store.Status()).Events).IsEqualTo(1);
        }
    }

    // A server that remembers nothing of a query has nothing to record a confirmation from, so it
    // answers in full: one answer more than it might have sent, and nothing unrecorded.
    [Test]
    public async Task AServerThatDoesNotRememberTheQueryAnswersItInFull()
    {
        await using var one = await Server.Start(database: null, extra: Conditional);
        await using var other = await Server.Start(database: null, extra: Conditional);

        using var first = await one.Get(holidays);
        using var elsewhere = await other.Get(holidays, first.Headers.ETag!.ToString());
        using var again = await other.Get(holidays, first.Headers.ETag!.ToString());

        using (Assert.Multiple())
        {
            await Assert.That(elsewhere.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(again.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That((await other.Store.Status()).Events).IsEqualTo(2);
        }
    }

    // A source left out of the record is confirmed as it was sent: with nothing recorded, and with
    // nobody needing a name.
    [Test]
    public async Task ASourceLeftOutIsConfirmedUnrecorded()
    {
        await using var server = await Server.Start(
            database: null,
            extra: _ =>
            {
                Conditional(_);
                _.Disclosure!.Exclude<Sample.Model.Holiday>();
            });

        using var first = await server.Get(holidays, user: null);
        using var second = await server.Get(holidays, first.Headers.ETag!.ToString(), user: null);

        using (Assert.Multiple())
        {
            await Assert.That(first.StatusCode).IsEqualTo(HttpStatusCode.OK);
            await Assert.That(second.StatusCode).IsEqualTo(HttpStatusCode.NotModified);
            await Assert.That((await server.Store.Status()).Events).IsEqualTo(0);
        }
    }
}
