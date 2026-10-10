# MCP

`Scry.Server.Mcp` serves Scry to AI agents over the [Model Context Protocol](https://modelcontextprotocol.io). An agent connected to it can read the schema and run queries. If the host allows it, the agent can also send commands.

The agent is a client like any other. What it may read is the allow-list, and what it sees of a row is what its user's row policies let through. Validation, limits and auditing all apply unchanged, because every tool is a thin transport over the same `ScryProcessor` that `MapScry` and `MapScryHub` use.


## Turning it on

MCP is off by default. It has three settings:

| `ScryOptions.Mcp` | Serves |
| --- | --- |
| `Off` (default) | Nothing. `MapScryMcp` maps no route. |
| `Read` | `describe_schema` and `query`. |
| `ReadWrite` | Those, plus `send_command`, `command_receipt` and `capabilities`. Needs [commands](commands.md) on (`MaxPendingCommands`), or the server refuses to start. |

<!-- snippet: scryOptionsMcp -->
<a id='snippet-scryOptionsMcp'></a>
```cs
/// <summary>
/// What an AI agent may do over MCP, where Scry.Server.Mcp is mapped. Default
/// <see cref="ScryMcpAccess.Off"/>, which maps no MCP route at all.
/// </summary>
/// <remarks>
/// <see cref="ScryMcpAccess.Read"/> serves the schema and queries;
/// <see cref="ScryMcpAccess.ReadWrite"/> adds commands, and so needs
/// <see cref="MaxPendingCommands"/> set, or the server refuses to start. Either way an agent is
/// held to exactly what any other client is: the allow-list, validation, row and command policies,
/// the limits and the audit.
/// </remarks>
public ScryMcpAccess Mcp { get; set; }
```
<sup><a href='/src/Scry.Server/ScryOptions.cs#L291-L304' title='Snippet source file'>snippet source</a> | <a href='#snippet-scryOptionsMcp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Register the MCP server beside `AddScry`:

<!-- snippet: addScryMcp -->
<a id='snippet-addScryMcp'></a>
```cs
/// <summary>
/// Registers the MCP server <c>MapScryMcp</c> maps, serving the tools
/// <see cref="ScryOptions.Mcp"/> allows. Needs <c>AddScry</c>.
/// </summary>
/// <remarks>
/// The server is stateless: each tool call is an HTTP request of its own, answered in that request's
/// scope, so a row policy reads the caller exactly as it does for <c>MapScry</c>.
/// </remarks>
public static IServiceCollection AddScryMcp(this IServiceCollection services)
{
    services.AddHttpContextAccessor();
    services.AddSingleton<IConfigureOptions<McpServerOptions>, ConfigureScryMcp>();
    services
        .AddMcpServer()
        .WithHttpTransport(_ => _.Stateless = true);
    return services;
}
```
<sup><a href='/src/Scry.Server.Mcp/ScryMcpExtensions.cs#L6-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-addScryMcp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Then map its endpoint:

<!-- snippet: mapScryMcp -->
<a id='snippet-mapScryMcp'></a>
```cs
/// <summary>
/// Maps the MCP endpoint at <paramref name="pattern"/>, where <see cref="ScryOptions.Mcp"/> is not
/// <see cref="ScryMcpAccess.Off"/>. Off, it maps nothing: a server serves agents because it said it
/// would.
/// </summary>
/// <remarks>
/// Runs the same startup checks <c>MapScry</c> does. Authorization goes on what this returns, and
/// applies to every tool; a row policy then narrows what that caller sees, as it does over HTTP.
/// </remarks>
public static IEndpointConventionBuilder MapScryMcp(this IEndpointRouteBuilder endpoints, string pattern)
{
    var services = endpoints.ServiceProvider;
    var options = services.GetRequiredService<ScryOptions>();
    services
        .GetRequiredService<ScryProcessor>()
        .EnsureReady(services);
    if (options.Mcp == ScryMcpAccess.Off)
    {
        return new NoEndpoints();
    }

    EnsureCommandsServed(options);
    return endpoints.MapMcp(pattern);
}
```
<sup><a href='/src/Scry.Server.Mcp/ScryMcpExtensions.cs#L26-L51' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScryMcp' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The sample does all three, at `/mcp`:

<!-- snippet: mapScryMcpSample -->
<a id='snippet-mapScryMcpSample'></a>
```cs
app.MapScryMcp("/mcp");
```
<sup><a href='/samples/Sample.WebServer/Program.cs#L149-L151' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapScryMcpSample' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## Connecting an agent

Any MCP client that speaks **streamable HTTP** can connect. Give it the URL `MapScryMcp` was mapped at, plus whatever credential the endpoint requires. The examples below use the sample's `http://localhost:5249/mcp`.

The server answers every request on its own, so a client needs no session affinity. The server also sends the agent its instructions when the client connects. Those tell the agent to call `describe_schema` first and to write queries as the JSON AST, so nothing else has to be put in the agent's prompt.

### Claude Code

```bash
claude mcp add --transport http scry http://localhost:5249/mcp
```

Where the endpoint requires authorization, pass the token as a header:

```bash
claude mcp add --transport http scry https://example.com/mcp --header "Authorization: Bearer <token>"
```

To share the server with everyone working in a repository, commit it as `.mcp.json` at the repository root instead:

```json
{
  "mcpServers": {
    "scry": {
      "type": "http",
      "url": "http://localhost:5249/mcp"
    }
  }
}
```

### Claude Desktop and claude.ai

Add it as a custom connector under **Settings → Connectors**. A connector is reached from Anthropic's side, so it needs a URL reachable from the internet, over HTTPS.

For a server running locally, bridge it through `mcp-remote` in `claude_desktop_config.json` instead:

```json
{
  "mcpServers": {
    "scry": {
      "command": "npx",
      "args": ["mcp-remote", "http://localhost:5249/mcp"]
    }
  }
}
```

### VS Code (GitHub Copilot agent mode)

In `.vscode/mcp.json`:

```json
{
  "servers": {
    "scry": {
      "type": "http",
      "url": "http://localhost:5249/mcp"
    }
  }
}
```

### Cursor

In `.cursor/mcp.json`, or `~/.cursor/mcp.json` for every project:

```json
{
  "mcpServers": {
    "scry": {
      "url": "http://localhost:5249/mcp"
    }
  }
}
```

### From .NET

The MCP SDK's own client, as the tests use it:

<!-- snippet: mcpClientConnect -->
<a id='snippet-mcpClientConnect'></a>
```cs
var transport = new HttpClientTransport(
    new()
    {
        Endpoint = new("http://localhost/mcp"),
        TransportMode = HttpTransportMode.StreamableHttp
    },
    httpClient,
    ownsHttpClient: true);
await using var client = await McpClient.CreateAsync(transport);

var schema = await client.CallToolAsync("describe_schema");
var answer = await client.CallToolAsync(
    "query",
    new Dictionary<string, object?>
    {
        ["root"] = "Ledger",
        ["pipeline"] = JsonDocument.Parse("""[{"$type":"count"}]""").RootElement
    });
```
<sup><a href='/IntegrationTests/McpTests.cs#L78-L97' title='Snippet source file'>snippet source</a> | <a href='#snippet-mcpClientConnect' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

### Checking it works

Ask the agent something the data answers, such as *"Which active employees are in Engineering?"*. It should call `describe_schema` once, then `query`. If it reports a refusal, the error names what was wrong, and a well-behaved agent corrects the query and asks again. A client that lists no tools is usually one of two things:

- `ScryOptions.Mcp` is still `Off`, so the route is not mapped and answers `404`.
- The endpoint's authorization refused the client (`401` or `403`).


## The tools

| Tool | Access | Does |
| --- | --- | --- |
| `describe_schema` | `Read` | Answers the [introspection](explorer.md#introspection) document: sources, types and members, enums and, under `ReadWrite`, commands. Under `Read` the commands are left out, so an agent that may not write is not told what writing would look like. |
| `query` | `Read` | Takes `root` (a source name) and `pipeline` (the operators, as the [wire format](wire-format.md) spells them). Answers the query's response: `kind`, `payload`, `stamp`. |
| `send_command` | `ReadWrite` | Takes `command` and `payload`. Answers the command's receipt: `Completed` with its `result`, `Failed` with its `error`, or `Pending` with the `id` to ask about. |
| `command_receipt` | `ReadWrite` | Takes that `id`. Answers the receipt as it stands. Only the caller that sent the command gets an answer. |
| `capabilities` | `ReadWrite` | Answers the commands this caller's policies allow at all. |

Every tool carries MCP's annotations: the read tools say they are read-only, and `send_command` says it writes.

### Writing a query

An agent writes a query as the JSON the wire carries. That is the same AST a generated client produces from LINQ, read by the same strict reader. The server's MCP instructions and the `query` tool's description teach it enough to start:

- the operator, node, function, operator and constant-tag names, read from the wire types themselves, so the text cannot drift from what the server accepts
- the two spellings of a member path and of a projection member
- worked examples

For instance, the three active employees first by name, with their department:

```json
{
  "root": "Employee",
  "pipeline": [
    {"$type": "where", "predicate": {"$type": "member", "path": "Active"}},
    {"$type": "orderBy", "key": {"$type": "member", "path": "Name"}, "descending": false},
    {"$type": "take", "count": 3},
    {"$type": "select", "projection": {"members": [
      "Name",
      {"name": "Department", "value": {"$type": "node", "node": {"$type": "member", "path": ["Department", "Name"]}}}
    ]}}
  ]
}
```

The agent never sends a version or a schema stamp. It writes against the server it is talking to, so the server fills in its own wire version.

### When something is refused

A refusal is an MCP tool error (`isError: true`) whose text is the same `ScryError` an HTTP endpoint answers with, `error` and `code`:

- A query refused for its own shape names what was wrong. Examples are an unknown operator (`WireFormat`) and a member off the allow-list (`Validation`). That is what lets an agent read the error, fix the query and ask again.
- A command is refused as the command endpoint refuses it: `Forbidden`, `NotFound` (one answer for a target that is absent, hidden or denied), `CommandLimit`, `PayloadTooLarge`.
- Anything else is the fixed `"Query execution failed."` or `"Command dispatch failed."`, and the real exception goes to the audit trail.


## Who is asking

The server is stateless. Each tool call is an HTTP request of its own, answered in that request's scope, so everything that decides who is asking works as it does for `MapScry`. That caller is also who the [disclosure audit](disclosure-audit.md), where it is on, records an agent's answers under: the schema it read, and each query's rows.

- authentication middleware
- `RequireAuthorization` on what `MapScryMcp` returns
- a scoped service a middleware fills from the user and a row policy reads
- `ScryOptions.Caller`, which a command is sent as and asked for again by

Unlike a hub, where a filter has to fill that scoped service for each call, nothing here needs adapting.

```cs
app.MapScryMcp("/mcp").RequireAuthorization();
```

MCP's own authorization is OAuth with bearer tokens. Prefer that to a cookie: a browser never attaches a bearer token on another site's behalf.


## What it does not do

- **Live queries, streams, batches, attachments.** An agent asks a question and reads an answer. `take` or `page` bounds how much comes back, and `MaxPageSize` caps it regardless.
- **C#.** The [explorer](explorer.md) compiles LINQ in the browser. The MCP transport accepts only the JSON AST, because compiling an agent's snippet on the server would run code the agent chose, in-process, ahead of every check. See [Security](security.md#ai-agents-over-mcp).
- **Anything beyond the allow-list.** An agent sees what `describe_schema` lists and nothing else, through the same row policies as its user.
