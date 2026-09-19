# Scry.Server.NServiceBus

Carries [Scry](https://github.com/Papyrine/Scry) change notifications over [NServiceBus](https://docs.particular.net/nservicebus/), so that what a message handler saves in another process re-asks the [live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md) that read it.

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

Docs: [Live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md)
