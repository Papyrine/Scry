# Scry.Server.MessagePipe

A [MessagePipe](https://github.com/Cysharp/MessagePipe) backplane for [Scry](https://github.com/Papyrine/Scry) [live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md): a write on one server node re-asks the live queries held by the others.

```cs
builder.Services
    .AddMessagePipe()
    .AddRedis(ConnectionMultiplexer.Connect("localhost:6379"));
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxSubscriptions = 1000;
        _.UseMessagePipeBackplane();
    });
```

The host registers MessagePipe and a distributed transport for it as it would for anything else, and this publishes and subscribes through whichever that is: Redis, NATS, an interprocess pipe. One adapter, and every transport MessagePipe has. For a host that already runs MessagePipe, it is the backplane that adds nothing new to operate.

A single server needs none of this, and nor does a deployment whose database can say when it was last written — see `Scry.Server.Delta`'s `UseDeltaChanges`.

What travels is a string naming entities, never rows, so MessagePipe's serializer is asked nothing of Scry's types, and whoever can publish under the topic can cause live queries to be asked again and nothing else.

Docs: [Live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md)
