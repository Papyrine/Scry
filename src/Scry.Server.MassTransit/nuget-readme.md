# Scry.Server.MassTransit

Carries [Scry](https://github.com/Papyrine/Scry) [commands](https://github.com/Papyrine/Scry/blob/main/docs/commands.md) over [MassTransit](https://masstransit.io/): a command a client sends is published to the consumer that handles it, and its outcome comes back to the client when the consumer is done.

On the Scry server:

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxPendingCommands = 100;
        _.UseMassTransitCommands(_ => _.For<RepriceOrder>());
    });
builder.Services.AddMassTransit(
    _ =>
    {
        _.AddScryCommandCompletions();
        _.UsingRabbitMq((context, bus) => bus.ConfigureEndpoints(context));
    });
```

On the worker:

```cs
builder.Services.AddMassTransit(
    _ =>
    {
        _.AddConsumer<RepriceOrderConsumer>();
        _.UsingRabbitMq(
            (context, bus) =>
            {
                bus.UseScryCommands(context);
                bus.UseMessageRetry(_ => _.Immediate(3));
                bus.ConfigureEndpoints(context);
            });
    });
```

The server validates, authorizes and binds the command as it would for an in-process handler, then publishes it with the command's id and caller as headers. The consumer is an ordinary `IConsumer<T>`. Once it is done, the worker publishes the outcome. Through the consume context on success, so with an outbox it leaves only if the consumer's work was kept. Through the bus on failure, once the failure leaves the filter, which is after retries are spent when `UseScryCommands` is configured before `UseMessageRetry`. A consumer of a command with a result answers with `context.SetScryResult(result)`.

It claims nothing until told: a MassTransit message is any class. Everything it does not claim stays with its in-process handler. Completions are consumed on a temporary endpoint per node, so the node that dispatched a command hears how it ended.

This package carries no change backplane. A consumer's writes reach live queries through the notification the server raises on completion for a targeted command, a change probe, or the poll.

Docs: [Commands](https://github.com/Papyrine/Scry/blob/main/docs/commands.md)
