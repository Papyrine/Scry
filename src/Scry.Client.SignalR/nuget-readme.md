# Scry.Client.SignalR

A [Scry](https://github.com/Papyrine/Scry) client whose queries travel over a SignalR hub connection rather than HTTP.

The reason to want it is [live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md): every one a page holds shares the one connection, where over HTTP each is a request held open.

```cs
var connection = new HubConnectionBuilder()
    .WithUrl("https://example.com/api/query-hub")
    .WithAutomaticReconnect()
    .Build();
await connection.StartAsync();

var query = new ScryQuery(ScrySignalRClient.Create(connection));
```

Everything written against a `ScryClient` works unchanged: the terminals, streaming, batching and live queries. A failure surfaces as the same exception it does over HTTP, so code that catches a rejection or a denial catches it here.

The connection is the caller's to build, start and dispose. Per-query headers are HTTP's and are refused, as they are over any custom transport, and attachments are fetched over HTTP.

The server half is `MapScryHub`, from `Scry.Server.SignalR`.

Docs: [Live queries](https://github.com/Papyrine/Scry/blob/main/docs/live-queries.md)
