# Scry.Server.NServiceBus

Everything [Scry](https://github.com/Papyrine/Scry) does over [NServiceBus](https://docs.particular.net/nservicebus/):

- **Changes**: what a message handler saves in another process re-asks the [live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md) that read it.
- **Commands**: a [command](https://github.com/Papyrine/Scry/blob/main/docs/commands.md) a client sends is carried to a worker, and its outcome comes back to the client when the worker replies.

## Changes

A Scry server sees its own saves through `ScryChangeInterceptor`. It cannot see a worker's. This is what tells it, with the entities that changed rather than only that something did.

On the worker endpoint:

```cs
builder.Services.AddScryNServiceBusBackplane();
builder.Services.AddDbContext<SampleContext>(
    (services, options) => options
        .UseSqlServer(connection)
        .AddInterceptors(services.GetRequiredService<ScryChangeInterceptor>()));

endpointConfiguration.UseScryChanges();
```

On the Scry server:

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxSubscriptions = 1000;
        _.UseNServiceBusBackplane();
    });
```

What each incoming message's handlers saved is published once, after they are done, through that message's own context. So the event leaves with the rest of what the handler sent, and only if the handler's work was kept: with the outbox, after its transaction commits. A handler that throws publishes nothing, and one that is retried publishes once.

NServiceBus delivers an event to one instance of each logical endpoint, since instances compete for the endpoint's queue. A worker's changes therefore reach every Scry server only where each server is an endpoint of its own. Scaled-out web nodes that share an endpoint name can leave the fan-out between themselves to the database's change marker — `Scry.Server.Delta`'s `UseDeltaChanges` — and use this for the worker's writes. A send-only endpoint receives nothing, so a server that is to hear changes cannot be one.

What travels names entities and never rows: whoever can publish the event can cause live queries to be asked again and nothing else.

## Commands

On the Scry server, beside the backplane:

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxPendingCommands = 100;
        _.UseNServiceBusCommands(_ => _.For<RepriceOrder>());
    });
```

On the worker endpoint:

```cs
endpointConfiguration.UseScryCommands();
```

The server validates, authorizes and binds the command as it would for an in-process handler, then sends it through its `IMessageSession`, routed by the endpoint's own routing, carrying the command's id and caller as headers. The worker's handler is an ordinary `IHandleMessages<T>`. Once the handlers are done, the worker replies through the message's own context, so the reply leaves only if the handler's work was kept, and once for a retried message. A message that exhausts recoverability is answered as failed as it goes to the error queue. A handler of a command with a result answers with `context.SetScryResult(result)`.

By default the server claims every command the endpoint knows as an NServiceBus `ICommand` by marker, plus those named with `For<T>()` (or all of them with `ForAll()`). Everything else stays with its in-process handler. The reply comes back to the endpoint that sent the command, so each node needs an endpoint of its own, as for the backplane, or `MakeInstanceUniquelyAddressable`.

Docs: [Live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md) · [Commands](https://github.com/Papyrine/Scry/blob/main/docs/commands.md)
