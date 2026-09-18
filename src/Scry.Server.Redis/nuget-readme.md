# Scry.Server.Redis

A Redis backplane for [Scry](https://github.com/Papyrine/Scry) [live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md): a write on one server node re-asks the live queries held by the others.

```cs
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect("localhost:6379"));
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxSubscriptions = 1000;
        _.UseRedisBackplane();
    });
```

A single server needs none of this, since it hears its own writes. Nor does a deployment whose database can say when it was last written — SQL Server and PostgreSQL, through `Scry.Server.Delta`'s `UseDeltaChanges` — because the database is then the backplane. This is for several nodes over any other database, and it composes with the rest: a backplane says which entities changed, where a change marker cannot.

What travels names entities and never rows. Whoever can write to the channel can cause live queries to be asked again, which costs what the throttle lets it cost, and nothing else: every answer still comes from running the query through its policies. Redis pub/sub is at most once, and a message a node misses costs a live query nothing worse than waiting for its poll.

A process that writes but serves no queries — a worker — registers the same backplane with `AddScryChanges()` and `AddScryRedisBackplane()`.

Docs: [Live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md)
