# Scry.Server.Mcp

Serves [Scry](https://github.com/Papyrine/Scry) to AI agents over the [Model Context Protocol](https://modelcontextprotocol.io).

It is off by default. It can be turned on read-only, which serves the schema and queries, or read-write, which adds commands.

```cs
builder.Services.AddScry<SampleContext>(
    _ =>
    {
        _.Mcp = ScryMcpAccess.Read;
    });
builder.Services.AddScryMcp();

app.MapScryMcp("/mcp").RequireAuthorization();
```

An agent is held to exactly what every other client is, because the same `ScryProcessor` handles its requests:

- validation and the allow-list
- row and command policies
- limits and auditing

It writes queries as Scry's JSON wire format, which the server reads as strictly as it reads a generated client's. It is never allowed to send C# for the server to compile. `MapScryMcp` runs the startup checks `MapScry` runs.

Docs: [MCP](https://github.com/Papyrine/Scry/blob/main/docs/mcp.md)
