# Sample

`/samples` contains a complete, runnable three-project solution: `Scry.Samples.slnx`.

| Project | Role |
| --- | --- |
| `Sample.Model` | EF Core model with the allow-list attributes. Referenced by the server, pointed at by path from the client. |
| `Sample.Server` | ASP.NET Core host: `DbContext`, `MapScry`, `MapScryExplorer`, and the Blazor host page. |
| `Sample.Client` | Blazor WebAssembly UI that writes LINQ against the generated models. |
| `Sample.Tests` | Snapshot tests over the rendered UI, the wire traffic, and the explorer endpoint. |

## Running it

The sample uses SQL Server LocalDB and creates/seeds the database on startup.

```bash
dotnet run --project samples/Sample.Server
```

Then browse to the URL it prints. The query explorer is at `/scry`.

## The model

Four sources covering each kind:

snippet: queryableEntity

`Salary` is `[QueryIgnore]`d, so it is absent from the generated client model and rejected if named
in a raw request.

snippet: queryableOrder

snippet: queryableView

snippet: queryablePoco

`EmployeeSummary` is a keyless entity over a database view, created by the seed routine.

snippet: dbContext

## The server

snippet: serverRegistration

`Holiday` has no table, so its data is registered explicitly. `MaxPageSize` is lowered from the
default 1000 to 200.

snippet: mapScry

snippet: mapExplorer

The sample always exposes the explorer so it can be browsed without setting an environment. A real
app should leave the default Development-only guard in place, or replace it with an authorization
check — see [Query explorer](explorer.md).

## The client

snippet: clientModelPath

snippet: clientGeneratorWiring

Because the sample uses project references rather than the NuGet package, the generator wiring that
`Scry.Client`'s `buildTransitive` props would normally supply is written out explicitly. See
[Source generator](source-generator.md).

snippet: clientRegistration

## The queries

The UI declares whatever shapes it wants:

snippet: clientProjectionTypes

A filter, an ordering, and a projection that reaches through two navigations:

snippet: clientQuery

A group-by with aggregates:

snippet: clientGroupBy

And a query parameterized by closure-captured locals — the values are evaluated client-side and sent
as constants, which is how an app builds a filtered query at runtime:

snippet: clientClosureCapture

## What the traffic looks like

`Sample.Tests` snapshots the actual HTTP exchange for the first query:

snippet: WireFormatTests.EmployeeQueryWireFormat.verified.txt

Four projected columns requested, four returned. `Salary` is neither requested nor returnable.

## Integration tests

`/IntegrationTests` hosts the same model over a real ASP.NET Core test server against LocalDB, and
exercises the full round trip through the typed client — including that a hand-crafted request naming
an ignored property is rejected:

snippet: rawRequestRejected
