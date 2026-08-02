# Query explorer

`Scry.Server.Explorer` is an opt-in, GraphiQL-style query explorer. It serves a self-contained Blazor WebAssembly UI that runs **Roslyn in the browser**: write strongly-typed C# LINQ against the allow-listed sources with real IntelliSense and diagnostics, see the serialized wire request, execute it, and inspect the results.

It is off unless mapped, and Development-only by default.

<img src="../samples/Sample.Tests/UiScreenshotTests.ExplorerRun.verified.png" border="1" alt="The explorer after running a query: the LINQ, the serialized wire request, the result table, and the raw response">


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

/// <summary>
/// Decides, per request, whether the explorer will show the SQL a query would run. Also
/// Development-only by default, and deliberately separate from <see cref="EnableGuard"/>: SQL
/// reveals more than the schema does — real table and column names, and the shape of any row
/// policy that narrowed the query — so opening the explorer to someone does not open this too.
/// </summary>
public Func<HttpContext, bool> EnableSqlPreview { get; set; } = DevelopmentOnly;
```
<sup><a href='/src/Scry.Server.Explorer/ScryExplorerOptions.cs#L6-L28' title='Snippet source file'>snippet source</a> | <a href='#snippet-explorerOptions' title='Start of snippet'>anchor</a></sup>
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
| `EnableSqlPreview` | `DevelopmentOnly` | Decides, per request, whether the [SQL preview](#sql-preview) is available. Separate from `EnableGuard` on purpose. |

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


## Working with a query

Three things the explorer does with the query in the editor, beyond running it.

**Share a link.** *Share* puts the query in the URL and copies the link. It rides in the **fragment** (`/scry/#q=…`), which browsers never send to the server — so a shared query cannot land in an access log, a proxy trace, or a referrer header on the way. Opening the link loads the query into the editor; a fragment that does not decode is ignored, and the explorer opens on its sample query rather than on an error.

**Export the results.** *Export CSV* saves the result table as it is displayed — same columns, same order, RFC 4180 quoting, with a UTF-8 BOM so Excel reads non-ASCII values correctly.

**See the SQL.** Covered next.


## SQL preview

*Show SQL* asks the server what the current query would run, **without running it**:

```sql
SELECT [e].[Name], [e].[Status]
FROM [Employees] AS [e]
WHERE [e].[Active] = CAST(1 AS bit)
```

The request is the same wire request *Run* would send, and the server puts it through the same pipeline — validation, the allow-list, [row policies](policies.md), the rebind onto EF — then reads the SQL back instead of executing. So nothing is previewable that would not have been runnable, and what is shown is the SQL that *would* run, policy predicates included.

Two consequences worth knowing:

- **Only a row-returning query has SQL to show.** A terminal (`CountAsync`, `FirstAsync`, `ToPageAsync`, …) is answered by executing it, so the server refuses one rather than running it behind a button labelled *preview*. Drop the terminal to see the SQL underneath. A [`[QueryablePoco]`](annotations.md) source has no SQL at all — its rows are supplied in memory.
- **It is guarded separately, and defaults to Development-only.** SQL reveals more than the schema does: real table and column names, and the *shape* of any row policy that narrowed the query — a tenant filter or soft-delete rule is right there in the `WHERE`. Opening the explorer to someone therefore does not open this to them; `EnableSqlPreview` is its own decision:

```cs
app.MapScryExplorer(options =>
{
    options.EnableGuard = _ => _.User.IsInRole("admin");
    // Still off for those admins unless this says otherwise.
    options.EnableSqlPreview = _ => _.User.IsInRole("dba");
});
```

When it is off the endpoint 404s like every other disabled route, and the UI does not offer the button — the introspection contract advertises the capability, so the explorer only shows what would work.

The same preview is available programmatically, on any host with a `ScryProcessor`:

```cs
var sql = processor.ToQueryString(request, services);
```


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
  SqlPreview: false,
  SchemaStamp: mi7QhupBDNZpcBYb
}
```
<sup><a href='/src/Scry.Tests/IntrospectionTests.Describe.verified.txt#L1-L543' title='Snippet source file'>snippet source</a> | <a href='#snippet-IntrospectionTests.Describe.verified.txt' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The contract carries only what tooling needs: source names and kinds, the generated model names, member names with the exact C# type spelling the source generator would emit, and the re-emitted enums. It carries **no** policies, resolvers, connection details, or CLR internals.

Because `TypeDisplay` matches the generator's emission exactly, the explorer can synthesize an identical set of query models in the browser, compile them with Roslyn, and offer completion against the real allow-listed surface — which is what makes this real IntelliSense rather than a word list:

<img src="../samples/Sample.Tests/UiScreenshotTests.ExplorerIntelliSense.verified.png" border="1" alt="Monaco's completion dropdown listing the allow-listed Employee members">


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

Because the explorer reveals the complete queryable schema, leaving it mapped in production means publishing that schema to anyone who passes the guard. The Development-only default is deliberate — and the [SQL preview](#sql-preview), which discloses more than the schema does, keeps a Development-only default of its own even when the explorer itself is opened up.


## The screenshots

The two images above are not files kept beside the docs: they are **Verify baselines**, and the markdown points its `<img>` straight at them. `ExplorerIntelliSense` and `ExplorerRun` in `samples/Sample.Tests/UiScreenshotTests.cs` drive the live explorer with Playwright and capture them, so a change to the UI fails a test rather than leaving a stale picture in the docs:

```bash
dotnet test samples/Scry.Samples.slnx --filter "FullyQualifiedName~UiScreenshotTests"
```

Accepting the received file — move `*.received.png` over `*.verified.png`, or accept it from the Verify diff tool — is what republishes the image. The fixture is `[Category("Browser")]` so a run can opt out, and pixel output is environment sensitive: a first run on a new OS or CI image is expected to need reseeding.

These two are laid out at **800** wide rather than at the width the fixture's other captures use, because a doc renderer shows them at native size and resampling a wider capture would soften every glyph of the small monospace text they are mostly made of. The run capture's query is broken across lines to fit that width unscrolled, so the LINQ reads in full.

Two things in Monaco move on their own and would otherwise put a column of different pixels between two identical runs, so both are settled before the shutter falls: the caret is pinned solid, and the scrollbar fade-out is waited for.

The frame around each image comes from an `<img border="1">` in the markdown rather than from the pixels. Note that a `style` attribute would not work here: GitHub's markdown sanitizer strips `style`, while `border` is on its allowed-attribute list.
