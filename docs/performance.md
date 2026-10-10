# Performance

What it costs the server to prepare a request and write a response, and the client to read one, in allocations and time.

Both sides carry two ways of doing the same work. The server can shape a result into dictionaries, serialize that into a `JsonElement`, and serialize the envelope around it a second time — or it can write the rows straight from the projected values into the response buffer, for one query or for every entry of a batch. The client can decode a response body to a string and read the payload through a `JsonElement` — or it can read the UTF-8 exactly as it arrived. The pairs produce identical bytes and identical results; what differs is what they spend getting there.

The HTTP endpoint and the typed client take the second of each pair. The benchmarks keep both arms so the difference stays visible and cannot quietly regress.


## Running them

```
dotnet run -c Release --project Benchmarks -- --filter '*'
```

Release is mandatory — BenchmarkDotNet refuses a Debug build. The project is deliberately in no solution; see the note in `CLAUDE.md` for why.

The sources are [`Benchmarks/ResponseBenchmarks.cs`](../Benchmarks/ResponseBenchmarks.cs), [`Benchmarks/PageBenchmarks.cs`](../Benchmarks/PageBenchmarks.cs), [`Benchmarks/BatchBenchmarks.cs`](../Benchmarks/BatchBenchmarks.cs), [`Benchmarks/TerminalBenchmarks.cs`](../Benchmarks/TerminalBenchmarks.cs), [`Benchmarks/PreparationBenchmarks.cs`](../Benchmarks/PreparationBenchmarks.cs) and [`Benchmarks/DisclosureBenchmarks.cs`](../Benchmarks/DisclosureBenchmarks.cs) for the server, and [`Benchmarks/ClientReadBenchmarks.cs`](../Benchmarks/ClientReadBenchmarks.cs) for the client. Rows come from an in-memory `[QueryablePoco]` source, so the measurement is shaping and serialization with no database and no I/O in it. The one exception is the journaled arm of the disclosure benchmark, which is there to price a flush to disk.


## Writing a response

Nine scalar members per row, which is a wide enough row that shaping and serialization dominate.

| Result | Rows | Dictionaries + `JsonElement` + serialize | Written from projected rows |
| --- | --- | --- | --- |
| List | 1000 | 2391 KB / 3097 µs | **905 KB / 1354 µs** |
| List | 100 | 256 KB / 602 µs | **143 KB** / 896 µs |
| List | 1 | 22 KB / 473 µs | 61 KB / 870 µs |
| Page | 1000 | 2433 KB / 3247 µs | **948 KB / 1779 µs** |
| Page | 100 | 269 KB / 967 µs | **157 KB** / 1199 µs |
| Page | 1 | 32 KB / 826 µs | 71 KB / 1134 µs |

A page costs what a list costs — 948 KB against 905 KB at a thousand rows — because it is the same rows through the same writer, with the `items`/`hasMore`/`cursor` envelope written around them. The rest of the gap is the ordering a page requires, which the list here does not ask for.

The general path serves every transport that is not the HTTP endpoint, which is what `ScryProcessor.Execute` returns, and the endpoint itself falls back to it for a result the writer cannot reproduce byte-for-byte. `FastWriterGoldenTests` pins that the two agree exactly.

What that fallback costs is a third arm of the same benchmark. Today the only result that takes it is the one answered to a drifted client — a request whose schema stamp disagrees with the server's, so the response carries the enum alias table — and at a thousand rows it allocates **2588 KB** against the writer's 905 KB. That is the general path's own cost plus the HTTP constant and nothing beyond it: the envelope is serialized into the same response buffer a written result fills, rather than into an array of its own for the single write that sends it.


### Outgrowing the buffer

A response that outgrows [`ResponseSpillThreshold`](server.md#response-size) — 64 KB by default — is drained to the response as it is written instead of grown to hold the whole result. A wide row here is a little over 200 bytes, so the thousand-row figures above measure the drained path and the hundred-row ones do not: the tables happen to straddle the threshold.

Total allocations are the same either way. The same bytes are produced and written; what differs is how many of them are resident at once. Held whole, a large list is resident twice over, as the projected rows and as the serialized bytes, and the buffer settles at the size of the result. Drained, the buffer settles at the threshold and the rows are pulled one at a time.

So this is a difference in peak memory that the allocation column cannot show, and it is not free: past the threshold a response gives up its `Content-Length` — a length can only describe a body that is already whole when the headers go out — and gives up answering a failure part-way through with a `500`. [Wire format](wire-format.md#response) sets out what a reader sees instead.

Everything the endpoint asks the database is asked asynchronously — the rows of a list or a page, a folded terminal, a denied-row probe, a cached policy's refresh, an attachment's bytes — so writing a response never holds a request thread on the database, and a caller that goes away cancels the round trip it was waiting on. That does not show here either: the benchmark reads an in-memory source, where there is nothing to await.


## Writing a batch

A batch is the same work repeated, so the entry count is what varies here and the rows per entry sit at a hundred.

| Entries | Rows each | Dictionaries + `JsonElement` + serialize | Written from projected rows |
| --- | --- | --- | --- |
| 1 | 100 | 256 KB / 640 µs | **144 KB** / 935 µs |
| 5 | 100 | 1280 KB / 3426 µs | **567 KB** / 3475 µs |
| 20 | 100 | 5117 KB / 12663 µs | **2146 KB** / 12623 µs |

Per entry, taken as the growth from 1 entry to 20, the general path costs 256 KB and the writer costs 105 KB — **−59%**. The clock is a wash at this width, for the reason the single-response table shows: a hundred rows is about where the two arms cross.

What separates the two is that the general path builds a dictionary per row and a `JsonElement` per entry, and the envelope then serializes every one of those elements a second time. The batch endpoint writes each entry's rows into the envelope as it goes, so the payload's bytes are produced once.

A batch also pays HTTP once for however many entries it carries, so the constant that dominates a single small response is divided across the batch.


## Folding to a value

A terminal — a count, an aggregate, a `First` — costs the same whatever the source holds, because there is no row count for it to scale with. That makes it invisible in a single request, where the measurement is almost entirely the fixed cost each arm carries. Carrying terminals as batch entries divides that constant out; the source is kept narrow so the pipeline work both arms pay identically does not bury the rest.

| Per entry | Dictionary + `JsonElement` + serialize | Written from the projected values |
| --- | --- | --- |
| Scalar | 9.6 KB | 10.0 KB |
| Single row | 24.3 KB | 26.2 KB |
| *List of one row, for comparison* | *19.8 KB* | *25.4 KB* |

Read the third row before the second. A list of one row goes through the same writer, and it carries the same gap — so what the second row shows is not a cost of terminals, but the fixed cost the writer pays for any result and amortizes over the rows in it. One row never amortizes it, whatever result shape the row arrived in. This is the same effect that makes the single-row entries of the tables above read the way they do.

What a terminal gets out of going through the writer is the rest of the table: it is one path for every result kind, so a terminal is written by the code the golden tests already hold to the general path byte for byte, rather than being the one shape that keeps its own.


## Preparing a request

What the server spends on a request before the database is asked: validating it, resolving its source, applying its policies, rebinding it onto EF, and planning its projection. `PreparationBenchmarks` prepares each request through `ScryProcessor.Stream` and drops the rows unread, so nothing executes and nothing crosses HTTP. The sources are entity sets on an unreachable context, so the composition goes through EF's own provider — which compiles nothing until a query is enumerated — rather than the in-memory provider the other benchmarks read, which compiles the whole tree on every enumeration and would bury this cost.

| Shape | Allocated |
| --- | --- |
| A predicate and a projection | 5.82 KB |
| Temporal reads, one through a nullable | 5.48 KB |
| A membership list | 4.95 KB |
| An inner join | 4.69 KB |
| A row policy | 5.88 KB |
| A deduplicated projection, ordered | 4.92 KB |
| The baseline carried into EF's translation | 13.05 KB |
| *The request's JSON alone, for comparison* | *4.17 KB* |

The whole of a preparation is a few microseconds and a few kilobytes, which is what a source generator on the server side could not improve on: nothing here is compiled per request, and the projection is the client's, so there is no shape to generate ahead of time. Nor is anything reflected per request. Resolving a source's set, composing an operator, reading a temporal part or an optional's `Value`, applying a policy and keying the row writer are each a delegate or a lookup made once.

The last row is the same request carried on into EF's pre-execution work — funcletizing, hashing, the compiled-query lookup, the command text — so the server's share can be read against the provider's. The table gives allocations only; the timings of these arms move by a third between runs, as the note below says, and the difference between two arms of two microseconds is inside that.


## The disclosure audit

What the [disclosure audit](disclosure-audit.md) adds to an answer. All three arms of `DisclosureBenchmarks` are the HTTP endpoint answering the same wide list, so they carry the same transport constant and can be read against each other as they stand, which the pairs above cannot.

| Rows | Audit off | Recorded, the sink discarding it | Recorded through a journal |
| --- | --- | --- | --- |
| 1 | 57 KB / 815 µs | 64 KB / 898 µs | 71 KB / 1221 µs |
| 100 | 139 KB / 825 µs | 210 KB / 1021 µs | 272 KB / 1583 µs |
| 1000 | 902 KB / 1451 µs | 1373 KB / 1823 µs | 1960 KB / 4318 µs |

**Off costs nothing that can be measured.** A host that never turned the audit on takes a null check where a capture would be made, and nothing else: no key is added to its projections and nothing is hashed. The endpoint arm of `ResponseBenchmarks`, run on the commit before the audit existed and again with the audit compiled in and off, allocated 124.92 KB against 124.58 KB at one row, 207.12 KB against 206.99 KB at a hundred, and 969.29 KB against 969.08 KB at a thousand, and the timings of the two runs differed by less than either run's own error. Both sides of that comparison were taken before the endpoint stopped initializing a context it did not use, which is why they read some 64 KB above every table here. The first column of this one reads a few kilobytes under the first table on the page because the hosts of this benchmark write no logs, where that one leaves the console logger on.

**Recording is paid per row.** The middle arm has the audit on over a sink that discards what it is handed, so it is the capture alone: each row's bytes hashed and copied into the batch, its key read from a slot the answer does not carry and written into the record, the batch built. Taken as the growth from 1 row to 1000:

| Per row | Audit off | Recorded | Through a journal |
| --- | --- | --- | --- |
| Allocated | 0.85 KB | 1.31 KB | 1.89 KB |
| Time | 0.64 µs | 0.93 µs | 3.10 µs |

So the capture adds about half a kilobyte and three tenths of a microsecond to a row a little over 200 bytes long, and about 7 KB to an answer whatever it holds. This row has no binary member, so it is serialized once either way: the bytes that are hashed are the bytes that are sent. A row with one is written a second time for the record, with the value named by its digest.

**A durable accept is the disk's to price.** The journal adds a write and a flush before the answer is sent, and the flush is most of the third column at a small answer: a third of a millisecond on this machine in this run and two thirds in the one before it, with the rest in proportion to what is written. It is the one figure on this page that says more about the machine than about the code, and the least steady. The SQL outbox pays a committed insert in the same place, which nothing here measures: it is a round trip to a database, and the benchmark has none.

Nor does the table show the database's share of recording. A key slot is a column added to the query, and a navigation a policy guards adds one correlated subquery for its key. The benchmark reads an in-memory source, where a key is a property read.


## Reading a response

| | Rows | Body as a string, payload via `JsonElement` | Body as the UTF-8 it arrived as |
| --- | --- | --- | --- |
| Response | 1000 | 1011 KB / 955 µs | **499 KB / 646 µs** |
| Response | 100 | 99 KB / 86 µs | **51 KB / 64 µs** |
| Response | 1 | 2.3 KB / 1.5 µs | **1.6 KB / 1.0 µs** |
| One streamed row | — | 1064 B / 1102 ns | **488 B / 403 ns** |

The client reads the bytes as they arrived. The string-and-`JsonElement` arm is measured beside it because that is the shape most transports reach for by default, and because the gap between them is what `QueryResponse` holding its payload as bytes until something asks for it buys.

At a thousand rows the reading arm makes no gen-2 collections at all, against 143.6 per 1000 operations for the other: nothing on that path is large enough or long-lived enough to reach the large object heap. That matters most in WebAssembly, and on any client whose process is long-lived enough for allocation to accumulate.


## Reading the numbers

**Allocations are the reliable figure.** They reproduce between runs to within a few bytes. Times move with whatever else the machine is doing — two runs of the same build here differed by a third or more on the wall clock (one arm by 43%) while the allocation columns were identical to the byte. Treat the timings as approximate and the ratios as more meaningful than the absolutes.

**Only the fast arm pays HTTP.** It goes through the real endpoint over a loopback round trip; the general-path arm calls the processor directly. So each fast row carries a fixed cost the row beside it does not, which is why at **one row it looks worse** — 2.8× the allocations for a list. Nothing is wrong there; the constant dominates. Read the growth from 1 row to 1000 instead, which is what each path adds per row:

| Per row | Dictionaries + `JsonElement` + serialize | Written from projected rows | |
| --- | --- | --- | --- |
| List | 2.37 KB / 2.63 µs | 0.85 KB / 0.48 µs | −64% / −82% |
| Page | 2.40 KB / 2.42 µs | 0.88 KB / 0.65 µs | −63% / −73% |

The crossover is around a hundred rows.

**These are one machine's numbers,** taken on Windows 11 with .NET 10.0.12 (x64, RyuJIT AVX2) under BenchmarkDotNet 0.15.2, on a developer machine rather than dedicated hardware. They are here to show the shape of the difference and to make a regression obvious, not as a specification. Re-run them rather than trusting them.


## Where the difference comes from

- **The writer walks the projection's shape.** The projection produces an `object[]` of the requested leaves, and the writer walks a name tree — member names camel-cased and JSON-escaped when the tree is built rather than per row — writing values straight out of that array.
- **A shape's writer is built once for the process.** The tree is held by shape, not by the plan that asked for it, so a projection sent a second time reuses the first one's writer. Building it is a node and an escaped name per member; over a thousand rows that is nothing, and over one row it would be the largest thing on the request. Shapes are the client's, so the table stops growing at a bound and a shape arriving past it builds its own writer for that request.
- **The payload's bytes are produced once.** The writer emits the complete envelope, version through stamp, as it goes. The general path serializes rows into a document and then serializes that document into the response, so the same bytes are produced twice with a parse in between; a batch pays that per entry, and its envelope is the second pass over all of them at once.
- **UTF-8 end to end.** A request and a response are both serialized to UTF-8 and read as UTF-8 on both sides. Decoding a body to a string transcodes the whole of it for the JSON reader to transcode straight back.
- **A response is read once, into the array it keeps.** The client asks for the headers first and reads the body straight off the connection into an array sized from the length the server declared; a binary part is read into its own array the same way. Left to HttpClient, a body is copied into a stream of its own and then out of it again. The declared length sizes the array and is never trusted for the read: a body that ends short of it is reported as a wire failure.
- **Pooled buffers.** A response is written into an array-pool buffer, which hands each intermediate back to the pool as it grows. A buffer that doubles from 256 bytes instead discards every intermediate on the way up, and the last of those are large enough to land on the large object heap.
- **A large response is drained rather than grown.** Past the threshold the buffer is written out and reset instead of doubling again, so a result of any size is held in at most the threshold's worth of buffer. Under it nothing changes: the response is written once, whole, declaring its length.

See [Wire format](wire-format.md) for what is actually written, and [Server](server.md) for where in the pipeline it happens.
