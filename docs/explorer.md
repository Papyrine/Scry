# Query explorer

`Scry.Server.Explorer` is an opt-in, GraphiQL-style query explorer. It serves a self-contained Blazor WebAssembly UI that runs **Roslyn in the browser**: write strongly-typed C# LINQ against the allow-listed sources with real IntelliSense and diagnostics, see the serialized wire request, execute it, and inspect the results.

It is off unless mapped, and Development-only by default.

<img src="images/explorer-run.png" border="1" alt="The explorer after running a query: the LINQ, the serialized wire request, the result table, and the raw response">


One screen shows the whole pipeline: the LINQ as written, the wire request it translated to, the rows the server returned, and the raw response envelope.


## Mapping it

```cs
app.MapScry("/api/query");
app.MapScryExplorer("/scry");
```

Then browse to `/scry`.

The explorer requires `AddScry` — it resolves the `ScryProcessor` to describe the schema.


## Options

<!-- snippet: explorerOptions -->
<a id='snippet-explorerOptions'></a>
```cs
/// <summary>Sub-path the explorer UI is served under. Default <c>/scry</c>.</summary>
public string Route { get; set; } = "/scry";

/// <summary>The existing <c>MapScry</c> query endpoint the explorer POSTs validated requests to.
/// Default <c>/api/query</c>.</summary>
public string QueryEndpoint { get; set; } = "/api/query";

/// <summary>
/// Decides, per request, whether the explorer is reachable. Defaults to Development-only:
/// the explorer reveals the full queryable schema, so it stays off in production unless a host
/// opts in explicitly (e.g. behind an admin authorization check).
/// </summary>
public Func<HttpContext, bool> EnableGuard { get; set; } = DevelopmentOnly;
```
<sup><a href='/src/Scry.Server.Explorer/ScryExplorerOptions.cs#L10-L24' title='Snippet source file'>snippet source</a> | <a href='#snippet-explorerOptions' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

<!-- snippet: mapExplorer -->
<a id='snippet-mapExplorer'></a>
```cs
app.MapScryExplorer(
    _ =>
    {
        _.Route = "/scry";
        // This sample always exposes the explorer. The default guard is Development-only — in a real
        // app, run in Development or set EnableGuard to your own check (e.g. an admin authorization).
        _.EnableGuard = _ => true;
    });
```
<sup><a href='/samples/Sample.Server/Program.cs#L54-L63' title='Snippet source file'>snippet source</a> | <a href='#snippet-mapExplorer' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

| Option | Default | Meaning |
| --- | --- | --- |
| `Route` | `/scry` | Sub-path the UI is served under. |
| `QueryEndpoint` | `/api/query` | The `MapScry` endpoint the explorer POSTs to. Must match the mapped route. |
| `EnableGuard` | `DevelopmentOnly` | Decides, per request, whether the explorer is reachable. |

To expose it outside Development, replace the guard with a custom check:

```cs
app.MapScryExplorer(options =>
{
    options.Route = "/scry";
    options.QueryEndpoint = "/api/query";
    options.EnableGuard = _ => _.User.IsInRole("admin");
});
```

When the guard returns false every explorer route returns **404**, not 403 — a disabled explorer is indistinguishable from one that was never mapped.


## Introspection

The UI reads the schema from `{Route}/introspect` on load. The same guard applies.

<!-- snippet: IntrospectionTests.Describe.verified.txt -->
<a id='snippet-IntrospectionTests.Describe.verified.txt'></a>
```txt
{
  Version: 1,
  MaxPageSize: 1000,
  Sources: [
    {
      Name: Announcement,
      Kind: Entity,
      Model: AnnouncementQueryModel
    },
    {
      Name: Asset,
      Kind: Entity,
      Model: AssetQueryModel
    },
    {
      Name: Building,
      Kind: Entity,
      Model: BuildingQueryModel
    },
    {
      Name: Department,
      Kind: Entity,
      Model: DepartmentQueryModel
    },
    {
      Name: DepartmentHeadcount,
      Kind: View,
      Model: DepartmentHeadcountQueryModel
    },
    {
      Name: Employee,
      Kind: Entity,
      Model: EmployeeQueryModel
    },
    {
      Name: Holiday,
      Kind: Poco,
      Model: HolidayQueryModel
    },
    {
      Name: Order,
      Kind: Entity,
      Model: OrderQueryModel
    },
    {
      Name: OrderLine,
      Kind: Entity,
      Model: OrderLineQueryModel
    },
    {
      Name: Post,
      Kind: Entity,
      Model: PostQueryModel
    },
    {
      Name: Region,
      Kind: Entity,
      Model: SalesRegionQueryModel
    },
    {
      Name: RegionSummary,
      Kind: View,
      Model: RegionSummaryQueryModel,
      Obsolete: 
    },
    {
      Name: Ticket,
      Kind: Entity,
      Model: TicketQueryModel
    },
    {
      Name: Vehicle,
      Kind: Entity,
      Model: VehicleQueryModel
    }
  ],
  Types: [
    {
      Model: AddressQueryModel,
      Members: [
        {
          Name: City,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Country,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: AnnouncementQueryModel,
      Members: [
        {
          Name: Pinned,
          TypeDisplay: bool,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        }
      ],
      Base: PostQueryModel
    },
    {
      Model: AssetQueryModel,
      Members: [
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Name,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: BuildingQueryModel,
      Members: [
        {
          Name: Floors,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        }
      ],
      Base: AssetQueryModel
    },
    {
      Model: DepartmentQueryModel,
      Members: [
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Name,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: DepartmentHeadcountQueryModel,
      Members: [
        {
          Name: Department,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Headcount,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false,
          Obsolete: Counts open roles too; use the Region rollup.
        }
      ]
    },
    {
      Model: EmployeeQueryModel,
      Members: [
        {
          Name: Active,
          TypeDisplay: bool,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Address,
          TypeDisplay: AddressQueryModel?,
          NeedsNullDefault: false,
          IsNavigation: true,
          IsCollection: false
        },
        {
          Name: Avatar,
          TypeDisplay: byte[],
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Department,
          TypeDisplay: DepartmentQueryModel?,
          NeedsNullDefault: false,
          IsNavigation: true,
          IsCollection: false
        },
        {
          Name: DepartmentId,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Manager,
          TypeDisplay: EmployeeQueryModel?,
          NeedsNullDefault: false,
          IsNavigation: true,
          IsCollection: false
        },
        {
          Name: ManagerId,
          TypeDisplay: int?,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Name,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: PreviousAddresses,
          TypeDisplay: global::System.Collections.Generic.IReadOnlyList<AddressQueryModel>,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: true
        },
        {
          Name: Status,
          TypeDisplay: Status,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: HolidayQueryModel,
      Members: [
        {
          Name: Date,
          TypeDisplay: global::System.DateOnly,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Name,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: OrderQueryModel,
      Members: [
        {
          Name: Amount,
          TypeDisplay: decimal,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Discount,
          TypeDisplay: decimal?,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Grade,
          TypeDisplay: char,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Lines,
          TypeDisplay: global::System.Collections.Generic.IReadOnlyList<OrderLineQueryModel>,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: true
        },
        {
          Name: Placed,
          TypeDisplay: global::System.DateTime,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Priorities,
          TypeDisplay: global::System.Collections.Generic.IReadOnlyList<Priority>,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: true
        },
        {
          Name: Quantity,
          TypeDisplay: uint,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Region,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Scores,
          TypeDisplay: global::System.Collections.Generic.IReadOnlyList<int>,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: true
        },
        {
          Name: Sku,
          TypeDisplay: ulong,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Tags,
          TypeDisplay: global::System.Collections.Generic.IReadOnlyList<string>,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: true
        }
      ]
    },
    {
      Model: OrderLineQueryModel,
      Members: [
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Order,
          TypeDisplay: OrderQueryModel?,
          NeedsNullDefault: false,
          IsNavigation: true,
          IsCollection: false
        },
        {
          Name: OrderId,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Price,
          TypeDisplay: decimal,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Quantity,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Sku,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: PostQueryModel,
      Members: [
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Name,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Published,
          TypeDisplay: bool,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: RegionSummaryQueryModel,
      Members: [
        {
          Name: Region,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Total,
          TypeDisplay: decimal,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        }
      ],
      Obsolete: 
    },
    {
      Model: SalesRegionQueryModel,
      Members: [
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Name,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: TicketQueryModel,
      Members: [
        {
          Name: Id,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: IsOpen,
          TypeDisplay: bool,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        },
        {
          Name: Name,
          TypeDisplay: string,
          NeedsNullDefault: true,
          IsNavigation: false,
          IsCollection: false
        }
      ]
    },
    {
      Model: VehicleQueryModel,
      Members: [
        {
          Name: Wheels,
          TypeDisplay: int,
          NeedsNullDefault: false,
          IsNavigation: false,
          IsCollection: false
        }
      ],
      Base: AssetQueryModel
    }
  ],
  Enums: [
    {
      Name: Priority,
      Values: [
        Low,
        High
      ]
    },
    {
      Name: Status,
      Values: [
        FullTime,
        PartTime,
        Contractor
      ]
    }
  ],
  QueryEndpoint: /api/query,
  SchemaStamp: mi7QhupBDNZpcBYb
}
```
<sup><a href='/src/Scry.Tests/IntrospectionTests.Describe.verified.txt#L1-L542' title='Snippet source file'>snippet source</a> | <a href='#snippet-IntrospectionTests.Describe.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The contract carries only what tooling needs: source names and kinds, the generated model names, member names with the exact C# type spelling the source generator would emit, and the re-emitted enums. It carries **no** policies, resolvers, connection details, or CLR internals.

Because `TypeDisplay` matches the generator's emission exactly, the explorer can synthesize an identical set of query models in the browser, compile them with Roslyn, and offer completion against the real allow-listed surface — which is what makes this real IntelliSense rather than a word list:

<img src="images/explorer-intellisense.png" border="1" alt="Monaco's completion dropdown listing the allow-listed Employee members">


Note what is offered and what is not: `Active`, `Department`, `Manager`, `Name`, `Status` — but no `Salary`, because it is `[QueryIgnore]`d and therefore never reaches the introspection contract.

The same contract can be produced programmatically:

```cs
var introspection = processor.Describe();
```


## How it works

Everything except two HTTP calls happens **in the browser**: the schema is fetched once, the model is synthesized and compiled with Roslyn locally, and only an already-translated wire request crosses to the server — the same endpoint any client POSTs to.

```mermaid
sequenceDiagram
    box Browser (Blazor WASM)
        participant UI as Explorer UI
        participant Synth as ModelSynthesizer
        participant Roslyn as RoslynWorkspace
        participant Exec as SnippetExecutor
    end
    participant Server

    UI->>Server: GET {Route}/introspect
    Server-->>UI: schema contract
    UI->>Synth: build query models from contract
    Synth->>Roslyn: same C# the generator emits
    Note over Roslyn: compiles in-browser —<br/>real IntelliSense + diagnostics
    UI->>Exec: user's LINQ snippet
    Note over Exec: compile, run vs capturing client,<br/>ToScryRequest (production translation)
    Exec-->>UI: serialized wire request
    UI->>Server: POST {QueryEndpoint}
    Note over Server: validated like any other request
    Server-->>UI: rows + raw response
```

1. On load the UI fetches `{Route}/introspect`.
2. `ModelSynthesizer` turns that contract into the same C# the design-time generator would emit — the enums, one query model per type, and a `ScryQuery` facade.
3. `RoslynWorkspace` compiles that source in-browser and wraps the user's expression in a method body, so the C# completion service offers members, and diagnostics are real compiler diagnostics.
4. `SnippetExecutor` compiles the expression, runs it against a capturing client to build the LINQ expression tree, and calls the production `ToScryRequest` — so the wire request shown is produced by exactly the same translation the real client performs.
5. The request is POSTed to `QueryEndpoint`, where it is validated like any other.

A trailing terminal (`.ToListAsync()`, `.FirstAsync()`, `.CountAsync()`, or a plain `.ToList()`) is recognised and folded into the wire request as its terminal operator.

Only validated requests reach the server. The explorer is a convenience over the same endpoint, not a bypass of it — it cannot query anything a normal client could not.


## Deployment notes

The UI is published and embedded as manifest resources inside the `Scry.Server.Explorer` assembly, so the package is fully self-contained: no static web assets manifest, no extra files to deploy, and nothing to configure beyond the route.

Because the explorer reveals the complete queryable schema, leaving it mapped in production means publishing that schema to anyone who passes the guard. The Development-only default is deliberate.


## Regenerating the screenshots

Unlike every other code block in these docs, the two images above are **not** merged from source at build time — they are captured from a real browser and committed under `docs/images/`. They will therefore drift silently when the explorer UI changes; refresh them when it does.

`ExplorerWalkthrough` in `samples/Sample.Tests/UiSnapshotTests.cs` drives the live explorer with Playwright and writes the raw captures to a temp directory (it is `[Explicit]`, so it does not run in a normal test pass, and the fixture is `[Category("Browser")]` so CI can opt out — pixel output is environment sensitive):

```bash
dotnet test samples/Sample.Tests --filter "FullyQualifiedName~ExplorerWalkthrough"
```

It prints the output directory. `1-loaded.png`, `2-intellisense.png`, `3-run.png`, `3b-count.png`, `4-hover.png`, and `5-dark.png` are captured; the two used here are `2-intellisense` and `3-run`.

Two post-processing steps are applied to each before committing:

1. Trailing whitespace trimmed (the captures are full-page, so most of the height is blank).
2. The empty interior of the Monaco editor box spliced out — it renders a fixed height regardless of how little code it holds.

The frame around each image comes from an `<img border="1">` in the markdown rather than from the pixels. Note that a `style` attribute would not work here: GitHub's markdown sanitizer strips `style`, while `border` is on its allowed-attribute list.
