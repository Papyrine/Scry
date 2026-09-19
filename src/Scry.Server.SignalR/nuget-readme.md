# Scry.Server.SignalR

Serves [Scry](https://github.com/Papyrine/Scry) queries over a SignalR hub, beside the HTTP endpoints or instead of them.

The reason to want it is [live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md). Over HTTP each one is a request held open, which HTTP/2 multiplexes and HTTP/1.1 caps at six per origin. Over a hub, every live query a page holds shares one connection. A host that already routes everything through SignalR, or scales out through Azure SignalR Service, gets Scry on the same path.

```cs
builder.Services.AddSignalR();
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.MaxSubscriptions = 1000;
    });

app.MapScryHub("/api/query-hub").RequireAuthorization();
```

Everything a query is subject to applies unchanged, because it is the same `ScryProcessor`: validation, the allow-list, row policies, auditing, and the limits on how many live queries may be open. `MapScryHub` runs the startup checks `MapScry` runs.

Requests and answers cross the hub as strings of the JSON the HTTP endpoints speak, so it is Scry's own reader that touches them and never the hub's serializer. The client half is `Scry.Client.SignalR`.

Docs: [Live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md)
