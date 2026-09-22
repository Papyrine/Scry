// An ordinary handler. Nothing in it mentions Scry: it saves, and the save is what gets reported.
// begin-snippet: sampleNServiceBusHandler
public sealed class RepriceOrderHandler(SampleContext data) :
    IHandleMessages<RepriceOrder>
{
    public async Task Handle(RepriceOrder message, IMessageHandlerContext context)
    {
        var order = await data
            .Orders
            .FindAsync([message.Id], context.CancellationToken);
        if (order is null)
        {
            return;
        }

        order.Amount += 1;
        await data.SaveChangesAsync(context.CancellationToken);
    }
}
// end-snippet
