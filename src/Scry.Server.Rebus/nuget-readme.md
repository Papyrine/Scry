# Scry.Server.Rebus

Carries [Scry](https://github.com/Papyrine/Scry) [commands](https://github.com/Papyrine/Scry/blob/main/docs/commands.md) over [Rebus](https://github.com/rebus-org/Rebus): a command a client sends is sent to the handler that handles it, and its outcome comes back to the client when the handler replies.

On the Scry server:

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxPendingCommands = 100;
        _.UseRebusCommands(_ => _.For<RepriceOrder>());
    });
builder.Services.AddRebusHandler<RebusCommandCompletedHandler>();
builder.Services.AddRebus(
    configure => configure
        .Transport(_ => _.UseRabbitMq(connection, "web-node-1"))
        .Routing(_ => _.TypeBased().Map<RepriceOrder>("worker")));
```

On the worker:

```cs
builder.Services.AddRebusHandler<RepriceOrderHandler>();
builder.Services.AddRebus(
    configure => configure
        .Transport(_ => _.UseRabbitMq(connection, "worker"))
        .Options(_ => _.EnableScryCompletion()));
```

The server validates, authorizes and binds the command as it would for an in-process handler, then sends it with the command's id and caller as headers, routed by the bus's own routing. The handler is an ordinary `IHandleMessages<T>`. Once the handlers have returned, the worker replies inside the message's own transaction, so the reply leaves only if the handler's work was kept, and once for a message that was retried. A message that exhausts its delivery attempts is answered as failed as it goes to the error queue. A handler of a command with a result answers with `MessageContext.Current.SetScryResult(result)`.

It claims nothing until told: a Rebus message is any class. Everything it does not claim stays with its in-process handler. The reply comes back to the input queue the command was sent from, so each node needs an input queue of its own.

This package carries no change backplane. A handler's writes reach live queries through the notification the server raises on completion for a targeted command, a change probe, or the poll.

Docs: [Commands](https://github.com/Papyrine/Scry/blob/main/docs/commands.md)
