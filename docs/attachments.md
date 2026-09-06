# Attachments

A `byte[]` marked [`[Attachment]`](annotations.md#attachment) is a **claim check**. The query never reads it; what comes back on each row is a handle carrying that row's key, which fetches the bytes on demand through an endpoint of its own.

The point is what a query costs. A document or a full-size image transferred with every row is paid for on every row, whether or not anything looks at it. An attachment moves that cost to the moment the value is actually wanted, and to the rows it is wanted for.

Its sibling, [`[BinaryTransfer]`](annotations.md#binarytransfer), makes the opposite trade — the value still travels with the row, and only its encoding improves. The [comparison table](annotations.md#attachment) is the short version of when to reach for which.

The [sample app](sample.md#the-photos-which-the-queries-never-carry) has one end to end: employee photos, drawn on its home page from handles the query brought back, with one employee holding no photo so the empty answer is visible beside the rest.


## The model

<!-- snippet: attachmentMember -->
<a id='snippet-attachmentMember'></a>
```cs
[Queryable]
[AttachmentWith(typeof(UnsealedContractsPolicy))]
public class Contract
{
    public int Id { get; set; }
    public string Name { get; set; } = "";

    // Never read by a query. A client sees a handle and fetches the bytes by this row's key, and the
    // declared content type is what that fetch is served as.
    [Attachment(ContentType = "application/pdf")]
    public byte[]? Document { get; set; }
}
```
<sup><a href='/src/Scry.Tests/TestModel.cs#L519-L532' title='Snippet source file'>snippet source</a> | <a href='#snippet-attachmentMember' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The check that authorizes it:

<!-- snippet: attachmentPolicy -->
<a id='snippet-attachmentPolicy'></a>
```cs
public sealed class UnsealedContractsPolicy :
    IAttachmentPolicy<Contract>
{
    /// <summary>The seeded row this refuses, so a denial is exercised without needing a header.</summary>
    public const int SealedId = 3;

    public bool Authorize(ScryAttachmentContext context) =>
        context.KeyValues is not [SealedId];
}
```
<sup><a href='/src/Scry.Tests/TestModel.cs#L538-L548' title='Snippet source file'>snippet source</a> | <a href='#snippet-attachmentPolicy' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Registered by the attribute above, or in code:

```cs
builder.Services.AddScry<SampleContext>(
    _ => _.AddAttachmentPolicy<Contract, UnsealedContractsPolicy>());
```

A source exposing an attachment with neither **refuses to start**. The endpoint is reached by row key, so leaving it unauthorized would hand out any row whose key can be guessed — and unlike the query endpoint, where a default-deny allow-list already stands between a caller and the data, there is nothing else here to say no.


## Querying

The generated member is a `ScryAttachment` rather than a `byte[]`, so the value has no place in a query at all:

```cs
// Every member the model declares — the handles come back with the rows.
var contracts = await Query.Contract.ToListAsync();

await using var document = await contracts[0].Document.OpenAsync();
```

`OpenAsync` returns a `Stream`, or **null** when the stored value is null. The caller owns the stream and disposes it; the response stays open until it does, so a large value streams rather than landing in memory whole.

A handle holds only the source name, the member name, and the row's key, so it outlives the response it came from. Nothing stops one being kept and opened much later — and nothing about that is privileged, because every fetch is authorized afresh.


## Projections must carry the key

An attachment is fetched **by its row's key**. A projection that keeps the handle therefore has to keep the key beside it:

```cs
// Fine: Id is the key Contract is fetched by.
.Select(_ => new {_.Id, _.Document})

// Refused: nothing left to fetch by.
.Select(_ => new {_.Name, _.Document})
```

The same rule one navigation down, where the key is the navigation's own:

```cs
.Select(_ => new {_.Name, Parent = new {_.Parent!.Id, _.Parent!.Document}})
```

This is a **build error** (`SCRY113`), not a runtime one, with the translator refusing the same query again if it was composed in a way the analyzer could not read. Two related rules go with it:

- `SCRY114` — an attachment is not a value, so it cannot be filtered, ordered, grouped, or computed on.
- `SCRY115` — an attachment cannot be carried through `Distinct`, `SelectMany`, a join, a set operation, or a `GroupBy`. Each rewrites what a row is, so a key projected beside a handle stops identifying one row of one source.

A query with no `Select` is always fine: the key members are part of the model.


## How the key is derived

The client is generated from the model assembly's **metadata**, which is read and never executed — so `OnModelCreating` is invisible to it. Both sides therefore derive the key from what is written on the type:

1. Every member marked `[Key]`.
2. Otherwise a member named `Id`.
3. Otherwise one named `{TypeName}Id`.

Ordered by name, which is the order the key values travel in — a composite key's declared order is not something metadata exposes, so it cannot be the canonical one.

At startup the server compares what it derived against the **real** EF primary key and refuses to start if they differ, naming `[Key]` as the fix. That check is what makes the convention safe: a fluently configured key cannot silently leave the client fetching by one key and the server keyed on another.


## Content type

The fetch is served as whatever the member declares, and `application/octet-stream` where it declares nothing:

```cs
[Attachment(ContentType = "image/png")]
public byte[]? Photo { get; set; }
```

Which matters for one reason: it is what a saved file is named from. The [explorer](explorer.md) and the [debug sidecar](sidecar.md) both offer a download beside a fetched attachment, and both take the extension from this — `.png` rather than `.bin`. Nothing derives it from the bytes; a fixed map turns the declared type into an extension, and a type not in that map is `.bin`, because a wrong extension is worse than a generic one.

A column holding files of differing types decides per row instead, from the attachment policy — which sees the key before the row is read:

```cs
public bool Authorize(ScryAttachmentContext context)
{
    context.ContentType = LookUpMediaType(context.KeyValues);
    return true;
}
```

`ScryAttachmentContext.ContentType` starts as what the member declared, and assigning to it overrides that for this fetch alone. Whichever answered, it comes back on `ScryAttachmentResult.ContentType`, so a transport of its own serves the same type the HTTP endpoint does.

Three things about this are deliberate:

- **The declaration is the server's, never the caller's.** It is written on the model, so no request can influence what a response is labelled.
- **`200`s are sent `X-Content-Type-Options: nosniff`.** A declared type is a statement about a *column*; the bytes stored under it are whatever was written there. A mislabelled response is then a mislabelled file rather than a browser deciding for itself what it is looking at.
- **A `200` is a download, never a document.** It carries `Content-Disposition: attachment` (named for the member and the declared type) and `Content-Security-Policy: sandbox`, and the endpoint accepts only `application/json` bodies. The endpoint answers `POST`, but an HTML form navigates a browser with `POST` too — and a `text/plain` form field can be shaped as JSON — so answering only that method is not what keeps a type that scripts as a top-level document (`image/svg+xml`, `text/html`) from running on the API's origin as whoever the browser sent. The three headers are. None of them touches a client that fetches the bytes programmatically, which every Scry client does.

Any media type may be declared, including ones a browser would script as a top-level document — `text/html`, `image/svg+xml`: the download headers above are what make them safe to serve, so no type is refused for what it is. A value that is not a `type/subtype` fails at startup rather than being served. The type a policy sets through `ScryAttachmentContext.ContentType` is held to the same rule where it is set, since it exists only once the policy runs: one that is not a media type is a fault naming the policy — the fixed `500` to the caller, the real message in the audit trail — and never a response header.


## Security

Three things stand between a caller and an attachment's bytes, and all of them apply:

1. **The attachment policy.** `IAttachmentPolicy<T>.Authorize` runs *before the database is touched*, receiving the member name, the parsed key values, the request services, and the headers. Mandatory, as above. Because it runs first, a refusal costs no read and so answers sooner than a row that is missing — the one thing that tells the two `404`s below apart, for a caller timing them. A policy that decides on the key alone accepts that; one that must not reads the row itself through `Db` before deciding, and then both answers cost a lookup.
2. **The source's [row policy](policies.md).** The fetch resolves its row through the same policy-filtered source a query does, so a row a query could not have returned is not one an attachment can be pulled from.
3. **Whatever guards the endpoint.** The attachment endpoint is mapped inside `MapScry`, so `.RequireAuthorization()` on what it returns reaches it along with the query, stream, and batch endpoints. A deployment cannot accidentally guard three of the four.

`ScryAttachmentContext.RequestHeaders` is client-supplied and therefore untrusted — hint data, exactly as it is for a row policy. Identity comes from the authenticated principal, resolved through `Services`.

**Refused, missing, and hidden are one answer.** A denial, a row that is not there, and a row a policy filters out all return `404` with no body. Telling them apart would make the endpoint an oracle for which rows exist, which is precisely what a caller holding a guessed key is asking.

The value being null is a *different* answer — `204`, meaning the row was readable and the column holds nothing.


## The wire

| Status | Meaning |
| --- | --- |
| `200` | The bytes, as `application/octet-stream`. |
| `204` | The row was readable; the value is null. |
| `404` | Refused, absent, or hidden — deliberately indistinguishable. |
| `400` | A malformed request, an unknown source or member, the wrong number of key values, or one that does not parse. |
| `500` | `Attachment fetch failed.`, and nothing more. |

The request shape and the endpoint are in [Attachment retrieval](wire-format.md#attachment-retrieval). The query wire is untouched by all of this: no operator, node, or version changed, and an attachment member is not addressable by a query at any of the endpoints — a hand-built request naming one is rejected with `400`.


## In the explorer

The [query explorer](explorer.md#working-with-a-query) adds a column per attachment to a result whose rows it can identify, with a *fetch* link that exchanges the row's key for the bytes and saves them as a file. It never materializes a row into a generated model, so there is no handle to open — it builds the request from the key column instead, refusing the same operators a client's own plan refuses. Every fetch goes through the checks below unchanged.


## Observability

An attachment fetch is audited like a query. Its [`ScryAuditEntry`](observability.md#the-audit-hook) carries `Attachment` instead of `Request`, and `Rows` is 1 when the value was handed over and 0 when it was withheld — so a run of withheld fetches is visible without recording which of the three refusals each one was. The activity is `scry.attachment {source}` and the duration histogram tags it `scry.result_kind: attachment`.

That is the signal worth alerting on: an attachment endpoint is reached by row key, so a client walking keys looks exactly like a run of `404`s.


## What attachments do not do

**Range requests.** A fetch returns the whole value. There is no `Range` support, so an attachment is not a video seek endpoint.

**Caching.** No `ETag`, no `Cache-Control`, no conditional requests. Every open is a fetch, and every fetch is authorized — which is the conservative default, since a cached attachment is one the policy no longer sees. Ordinary ASP.NET Core middleware can add caching where a deployment wants it.

**Uploads.** Attachments are read-only, like the rest of Scry.

**A second copy of the allow-list.** An attachment member is invisible to queries and reachable only through its own endpoint. It is not a back door into an unexposed column: the member is allow-listed exactly as any other is, and `[QueryIgnore]` still hides it completely.

**Cover a type EF does not map.** `[Queryable]` is allowed on a type with no `DbSet`, and the startup key check skips a type absent from the model — so an attachment on one is only caught when a fetch reaches EF and fails, as a `500`.
