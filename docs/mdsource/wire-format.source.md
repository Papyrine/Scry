# Wire format

`Scry.Wire` defines the serializable query AST shared by client and server. It is a **restricted,
closed node vocabulary** — not general expression-tree serialization — which is what makes every
query exhaustively validatable.

You rarely construct these types by hand; the client translator emits them and the server consumes
them. This page is the reference for anyone writing another client, debugging a request, or
reviewing the surface.

## Serialization

All (de)serialization goes through `ScryJson`, whose options are part of the contract:

- Property names are **camelCase**.
- Dictionary keys (result rows) are **camelCase**.
- Enums are written as **names**, never numbers, with no naming policy applied.
- Null-valued **properties** are omitted — so optional AST members such as `predicate` or a null
  constant's `value` simply do not appear. Result rows are dictionaries, not properties, so an
  explicit `null` column is still written.
- Polymorphic types use a `$type` discriminator.
- Deserialization is **fail-closed**: unknown discriminators and malformed JSON throw
  `ScryWireException`, they are not skipped.

## Request

snippet: wireRequest

```json
{
  "version": 1,
  "root": "Employee",
  "pipeline": [ ... ]
}
```

| Field | Meaning |
| --- | --- |
| `version` | Wire format version. The server rejects anything newer than it understands. |
| `root` | The source name — the allow-listed type name, or the attribute's [`Name`](annotations.md#naming-a-source) when set. |
| `pipeline` | Ordered operators, applied left to right. |

Because `root` is part of the contract, prefer setting `Name` over relying on the type name if the
CLR type is likely to be renamed.

`QueryRequest.Create(root, pipeline)` stamps the current version.

## Operators

snippet: wireOperators

| `$type` | Payload | Meaning |
| --- | --- | --- |
| `where` | `predicate: Expr` | Filter. |
| `orderBy` | `key: Expr`, `descending: bool` | Primary ordering. |
| `thenBy` | `key: Expr`, `descending: bool` | Secondary ordering. Must follow `orderBy`. |
| `skip` | `count: int` | Skip elements. |
| `take` | `count: int` | Take at most `count`, capped by `MaxPageSize`. |
| `groupBy` | `keys: Expr[]` | Group. Exactly one key is supported. |
| `select` | `projection: Projection` | Project to the requested shape. |
| `count` | — | Terminal, scalar. |
| `any` | `predicate: Expr?` | Terminal, scalar. |
| `first` | `orDefault: bool`, `predicate: Expr?` | Terminal, single. |
| `single` | `orDefault: bool`, `predicate: Expr?` | Terminal, single. |

At most one terminal, and nothing may follow it.

## Expressions

snippet: wireExpressions

### `member`

```json
{ "$type": "member", "path": ["Manager", "Name"] }
```

A navigation path of allow-listed property names. Each segment is validated against the allow-list
of the type reached so far; every non-final segment must be a reference navigation.

### `const`

```json
{ "$type": "const", "value": "FullTime", "tag": "Enum" }
```

`value` is the invariant-culture string form, omitted entirely for a null constant. `tag` describes
the shape the client had:

snippet: wireTypeTags

The tag is a hint, not an instruction. The server parses the value into the **member's** type at the
comparison site, so `tag` never dictates what CLR type is constructed. Types with no dedicated tag
(`TimeOnly`, `TimeSpan`, `DateTimeOffset`, `char`) travel as `String` and are reconciled the same
way.

### `binary` and `unary`

```json
{
  "$type": "binary",
  "op": "GreaterThan",
  "left":  { "$type": "member", "path": ["Amount"] },
  "right": { "$type": "const", "value": "100", "tag": "Decimal" }
}
```

snippet: wireBinaryOps

When one side is a constant, its type is inferred from the other side, and nullable/non-nullable
operands are coerced to match.

### `call`

```json
{
  "$type": "call",
  "function": "StringStartsWith",
  "target": { "$type": "member", "path": ["Name"] },
  "arguments": [ { "$type": "const", "value": "A", "tag": "String" } ]
}
```

snippet: wireFunctions

There is no free-form method name anywhere in the format. This enum is the complete set of behaviour
a client can request.

### `aggregate`

```json
{ "$type": "aggregate", "function": "Sum", "selector": { "$type": "member", "path": ["Amount"] } }
```

snippet: wireAggregates

`selector` is omitted for `Count`. An aggregate is valid **only** as a projection member in a `select`
that follows a `groupBy`.

## Projections

```json
{
  "projection": {
    "members": [
      { "name": "Name", "value": { "$type": "expr", "expression": { "$type": "member", "path": ["Name"] } } }
    ]
  }
}
```

snippet: wireProjectionValues

| `$type` | Payload | Produces |
| --- | --- | --- |
| `expr` | `expression: Expr` | A scalar leaf, or an aggregate in a grouped select. |
| `nested` | `path: string[]`, `projection: Projection` | A nested JSON object built from a navigation. |

A projection must have at least one member. Nested projections are not allowed in a grouped select,
and nesting depth is capped by `MaxNavigationDepth`.

## Response

snippet: wireResponse

```json
{
  "version": 1,
  "kind": "List",
  "payload": [ { "name": "Alice", "status": "FullTime" } ]
}
```

| `kind` | `payload` |
| --- | --- |
| `List` | An array of projected row objects. |
| `Single` | One projected row object, or `null`. |
| `Scalar` | A bare value (`int` for `count`, `bool` for `any`). |

The client checks that `kind` matches the terminal it sent and throws `ScryWireException` if it does
not.

## Versioning

snippet: wireVersion

`QueryRequest.Create` and `QueryResponse.Create` stamp the current version. The server rejects a
request whose `version` is **greater** than its own — a newer client against an older server fails
closed rather than being partially understood. Older requests continue to be accepted.

The `ScryJson` options, the `$type` discriminator strings, and the enum member names are all part of
the contract. Changing any of them is a wire break.

## Worked example

This LINQ:

snippet: translateWithoutExecuting

translates to:

snippet: ClientRoundTripTests.ToScryRequestTranslatesWithoutExecuting.verified.txt

Note that `wanted`, `prefix`, and `take` were closure-captured locals; they are evaluated on the
client and emitted as constants.

Over HTTP, request and response look like this:

snippet: WireFormatTests.EmployeeQueryWireFormat.verified.txt
