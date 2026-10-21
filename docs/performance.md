# Performance

What it costs the server to prepare a request and write a response, and the client to read one, in allocations and time.

Both sides carry two ways of doing the same work. The server can shape a result into dictionaries, serialize that into a `JsonElement`, and serialize the envelope around it a second time — or it can write the rows straight from the projected values into the response buffer, for one query or for every entry of a batch. The client can decode a response body to a string and read the payload through a `JsonElement` — or it can read the UTF-8 exactly as it arrived. The pairs produce identical bytes and identical results; what differs is what they spend getting there.

The HTTP endpoint and the typed client take the second of each pair. The benchmarks keep both arms so the difference stays visible and cannot quietly regress.


## Running them

```
dotnet run -c Release --project Benchmarks -- --filter '*'
```

Release is mandatory — BenchmarkDotNet refuses a Debug build. The project is deliberately in no solution; see the note in `CLAUDE.md` for why.

The sources are [`Benchmarks/ResponseBenchmarks.cs`](../Benchmarks/ResponseBenchmarks.cs), [`Benchmarks/PageBenchmarks.cs`](../Benchmarks/PageBenchmarks.cs), [`Benchmarks/BatchBenchmarks.cs`](../Benchmarks/BatchBenchmarks.cs), [`Benchmarks/TerminalBenchmarks.cs`](../Benchmarks/TerminalBenchmarks.cs), [`Benchmarks/PreparationBenchmarks.cs`](../Benchmarks/PreparationBenchmarks.cs) and [`Benchmarks/DisclosureBenchmarks.cs`](../Benchmarks/DisclosureBenchmarks.cs) for the server, and [`Benchmarks/ClientReadBenchmarks.cs`](../Benchmarks/ClientReadBenchmarks.cs) for the client. Rows come from an in-memory `[QueryablePoco]` source, so the measurement is shaping and serialization with no database and no I/O in it. The exceptions are the journaled arm of the disclosure benchmark, which is there to price a flush to disk, and [`Benchmarks/DisclosureReadBenchmarks.cs`](../Benchmarks/DisclosureReadBenchmarks.cs), which asks its questions of a database.


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

So the capture adds about half a kilobyte and three tenths of a microsecond to a row a little over 200 bytes long, and about 7 KB to an answer whatever it holds. This row has no binary member, so it is serialized once either way: the bytes that are hashed are the bytes that are sent. A row with one is written a second time for the record, with the value named by its digest. So is a row with a value the host [left out of the record](disclosure-audit.md#leaving-a-member-out), with a marker in the value's place. Neither is measured here.

**A durable accept is the disk's to price.** The journal adds a write and a flush before the answer is sent, and the flush is most of the third column at a small answer: a third of a millisecond on this machine in this run and two thirds in the one before it, with the rest in proportion to what is written. It is the one figure on this page that says more about the machine than about the code, and the least steady. The SQL outbox pays a committed insert in the same place, which nothing here measures: it is a round trip to a database, and the benchmark has none.

Nor does the table show the database's share of recording. A key slot is a column added to the query, and a navigation a policy guards adds one correlated subquery for its key. The benchmark reads an in-memory source, where a key is a property read.

### Asking the record

What the questions the record exists for cost to ask of the SQL Server store, from `DisclosureReadBenchmarks`. This is the one benchmark with a database in it: LocalDB on the machine the rest ran on, over three records made by hand. `Repeated` is one answer of a thousand rows given five hundred times. `Drifting` is five hundred answers of a thousand rows, each one row different from the one before, which is what a live query leaves behind. `Small` is ten thousand answers of one row each. A question that is answered a page at a time is read to its end, two hundred lines a page: who received a row of the two large records is five hundred lines, three pages, and what a caller of the small record received is a thousand, five, which is most of why the small record is the slow one for the caller's questions.

Each cell is the store as it kept a list whole, then the store as it keeps a list as [runs other lists share](disclosure-audit.md#sql-server):

| Question | Repeated | Drifting | Small |
| --- | --- | --- | --- |
| Who received a row | 9.7 ms / 7.1 ms | 18.1 ms / 8.1 ms | 2.3 ms / 1.4 ms |
| What a caller received | 0.6 ms / 0.5 ms | 0.8 ms / 0.5 ms | 15.4 ms / 13.2 ms |
| Whether a caller was sent a member | 1.2 ms / 1.1 ms | 1.2 ms / 1.3 ms | 18.1 ms / 18.9 ms |
| An answer put back together | 8.4 ms / 5.8 ms | 8.7 ms / 8.2 ms | 0.27 ms / 0.16 ms |

**Reading through runs costs nothing that shows here.** A unit's place in an answer is added up from where its batch, its list and its run start, which is two more joins than a list kept whole needed, and no question is slower for it by more than the runs differ between themselves. These are timings of a database on a developer's machine and they move: the same build measured twice, a few minutes apart, gave figures a third apart and in one case two thirds apart. So the table says the two are alike, and does not say which is quicker. The one gain that is the code's is the first row of it. Asking who received a row has to count a row once where a unit was read from it twice, and that check now looks within the run the unit is in, where it used to search every answer the row was in.

**Every segment kept is another place to look, unless the question says when.** The record is since kept in [segments](disclosure-audit.md#sql-server), and every question is asked of a view over all of them. The figures for who received a row, here and in the two tables after, are from before [a row's receipts were filed in time order](#a-rows-receipts-in-time-order), which changed that question and no other. The same three records, with their answers shared out evenly between one segment, six and twenty-four, in the order they were given:

| Question | Record | 1 segment | 6 | 24 |
| --- | --- | --- | --- | --- |
| Who received a row | Repeated | 7.7 ms | 13.5 ms | 37.6 ms |
| | Drifting | 10.0 ms | 15.1 ms | 36.7 ms |
| | Small | 1.6 ms | 3.2 ms | 14.6 ms |
| What a caller received | Repeated | 0.46 ms | 0.52 ms | 0.73 ms |
| | Drifting | 0.46 ms | 0.53 ms | 0.73 ms |
| | Small | 12.3 ms | 18.5 ms | 21.2 ms |
| Whether a caller was sent a member | Repeated | 1.9 ms | 2.9 ms | 12.7 ms |
| | Drifting | 1.8 ms | 4.3 ms | 22.8 ms |
| | Small | 21.9 ms | 37.2 ms | 93.0 ms |
| An answer put back together | Repeated | 5.4 ms | 5.5 ms | 5.5 ms |
| | Drifting | 7.6 ms | 6.8 ms | 5.8 ms |
| | Small | 0.17 ms | 0.17 ms | 0.22 ms |

Putting an answer back together costs the same however many segments there are: which segment holds it is asked first, of all of them, and everything after is asked of that one. The other three are asked of every segment and grow with their number, though by less than it: who received a row is some four times dearer over twenty-four segments than over one, and what a caller received hardly dearer at all, being one seek of an index in each. Whether a caller was sent a member grows most, since each of the caller's answers is then looked into, and the database looks in every segment for it though told it is in the answer's own.

That last is how the database reads a view over the segments. A segment is left out only where its number is written into the statement. Told that a row's segment is the segment of some other row, or handed the number as a parameter, it looks in all of them and finds nothing in most. So an answer is put back together by a statement written for its one segment, and a question that covers them all pays for them all. Writing that question as one statement per segment, joined into one, so that what is asked of each row is asked of its own segment, was tried: it was no quicker, and over twenty-four segments of the repeated record took some three times as long, against the same run's one segment, as the view did.

**A question about a span of time is asked only of the segments that cover any of it.** A segment's span is written down when it is rolled away from, and a question with a span names the segments it can reach. Asked for the last twentieth of the time the record covers:

| Question | Record | 1 segment | 6 | 24 |
| --- | --- | --- | --- | --- |
| Who received a row, lately | Repeated | 1.4 ms | 0.52 ms | 0.95 ms |
| | Drifting | 2.0 ms | 0.71 ms | 1.0 ms |
| | Small | 1.2 ms | 0.41 ms | 6.3 ms |
| What a caller received, lately | Repeated | 0.25 ms | 0.09 ms | 0.14 ms |
| | Drifting | 0.43 ms | 0.10 ms | 0.13 ms |
| | Small | 1.2 ms | 0.92 ms | 0.93 ms |

So over twenty-four segments, who received a row in that span is a millisecond where over all time it is thirty-seven. With segments it is quicker than without, since the one or two segments reached are a fraction of the record. A listing read a page at a time gains the same way from its second page on: a page begins where the last one ended, which is a span of time.

A segment also keeps its own copy of what it shares. The repeated record is 2.6 MB in one segment, 13 MB in six and 52 MB in twenty-four, since each segment holds the thousand rows once and their content once; the drifting one is 20 MB, 24 MB and 58 MB, and the small one 15 MB, 20 MB and 36 MB. So segments are for retiring what is old, and are not free: a few long ones cost less to read and to keep than many short ones.

**What runs save is space, and only where answers are alike without being the same.**

| Record | Kept as whole lists | Kept as runs | Moved on in |
| --- | --- | --- | --- |
| Repeated | 2.3 MB | 2.5 MB | 2.8 s / 1.5 s |
| Drifting | 179 MB | 22 MB | 10.0 s / 2.8 s |
| Small | 14 MB | 11 MB | 3.7 s / 3.7 s |

The drifting record is five hundred thousand unit rows kept whole and thirty-two thousand kept as runs: the first answer's thousand, and for each answer after it the run or two its changed row disturbs, some sixty units, with a line for each of the twenty-five runs the list is made of. A repeated answer was already one list either way and gains nothing, and an answer of one row is a run of one unit. The last column is what the mover took to bring the whole record from the outbox into the tables, once each, and is as unsteady as the timings above.

### At a million answers

The same questions, from `DisclosureScaleBenchmarks`, of a record the size a busy host makes: a million answers from a thousand callers, of one to ten rows each out of a hundred thousand, a second apart, so some eleven and a half days of them. Every row is in some fifty answers, and the row asked about is in ten thousand. Each caller has a thousand. A listing is asked for its first page, two hundred lines, as a screen asks it, and the row's receipts are also read to the end, as an export reads them. Each is asked with the database doing nothing else, and then while a second record in the same database takes answers from four writers and is moved on as it goes: a few thousand answers in the time each case ran.

Each cell is idle / writing:

| Question | 1 segment | 6 | 24 |
| --- | --- | --- | --- |
| Who received a row | 63 ms / 69 ms | 107 ms / 116 ms | 111 ms / 122 ms |
| Every receipt of that row | 2.1 s / 2.4 s | 3.4 s / 3.9 s | 3.2 s / 3.4 s |
| What a caller received | 1.2 ms / 1.7 ms | 2.0 ms / 2.6 ms | 2.0 ms / 2.3 ms |
| Who received a row, lately | 21 ms / 25 ms | 6.0 ms / 8.0 ms | 19 ms / 20 ms |
| What a caller received, lately | 0.40 ms / 0.62 ms | 0.44 ms / 0.66 ms | 0.58 ms / 0.85 ms |
| Whether a caller was sent a member | 3.0 ms / 3.5 ms | 5.7 ms / 6.5 ms | 13 ms / 16 ms |
| An answer put back together | 0.23 ms / 0.43 ms | 0.26 ms / 0.52 ms | 0.26 ms / 0.45 ms |

**A record a hundred times the size asks no more of the questions about a caller or an answer.** What a caller received, whether it was sent a member and an answer put back together cost here what they cost of the records a hundredth the size above, segments and all: each is a seek of an index to what the caller or the answer holds, and the rest of the record is never read.

**What a row costs was what the row has.** Measured here, who received a row had to find every answer it is in before it could say which are the newest two hundred: ten thousand here, against five hundred above. So its first page was a twentieth of a second, and each later page found them all again, which is why reading all fifty pages took two seconds and more, some forty times one page. That is what [filing a row's receipts in time order](#a-rows-receipts-in-time-order) is for.

**Segments cost a row's question half as much again, and then little more.** Six segments and twenty-four cost the row's question alike, about seventy per cent more than one, and the member question grows with them most, as it does at the smaller size. A span is asked of the segments that cover it. In twenty-four, the last twentieth is the last segment and the one before it, where in six it is the last alone, which is the difference between those two columns of the lately row.

**Another record being written costs a tenth to a fifth.** The database had four writers and a mover to serve besides, and every question took a little longer for it, the questions that take a millisecond most in proportion. None changed in kind.

Filled, the record is 1.2 GB in one segment, 2.8 GB in six and 4.1 GB in twenty-four, and took ten to thirteen minutes to accept and move on. Its answers repeat every hundred thousand, so in one segment each list is kept once for ten answers. In more segments it is kept once per segment, since segments share nothing, and that is most of the growth. LocalDB's limit is ten gigabytes a database, so the next size up is a server's.


### A row's receipts in time order

Who received a row is since read from `DisclosureReceipt`, which files each row a unit was read from by when its answer was given, newest first, and a page is read in two steps: the page of receipts from that table alone, then what was sent in them from each segment the page reaches, named in the statement. Measured in one session on one machine, the commit before receipts against the commit after, the database idle.

At a million answers:

| Question | 1 segment | 6 | 24 |
| --- | --- | --- | --- |
| Who received a row | 63 ms → 4.5 ms | 107 ms → 5.5 ms | 111 ms → 13 ms |
| Every receipt of that row | 2.1 s → 0.26 s | 3.4 s → 0.29 s | 3.2 s → 0.64 s |
| Who received a row, lately | 21 ms → 4.9 ms | 6.0 ms → 5.2 ms | 19 ms → 7.8 ms |

And of the three smaller records, a row in five hundred answers in the first two and in a hundred in the third, read to its end:

| Question | Record | 1 segment | 6 | 24 |
| --- | --- | --- | --- | --- |
| Who received a row | Repeated | 11 ms → 11 ms | 18 ms → 17 ms | 49 ms → 34 ms |
| | Drifting | 13 ms → 9.9 ms | 18 ms → 13 ms | 44 ms → 32 ms |
| | Small | 2.3 ms → 2.9 ms | 4.3 ms → 5.6 ms | 17 ms → 13 ms |
| Who received a row, lately | Repeated | 1.7 ms → 1.4 ms | 1.0 ms → 1.4 ms | 1.3 ms → 6.3 ms |
| | Drifting | 2.6 ms → 0.85 ms | 1.1 ms → 0.89 ms | 1.3 ms → 7.0 ms |
| | Small | 1.4 ms → 0.68 ms | 0.59 ms → 3.5 ms | 7.3 ms → 6.9 ms |

**A page is now a page's worth of work.** A row in ten thousand answers is a page in a few milliseconds and all of it in a quarter of a second, against a twentieth of a second and seconds. Twenty-four segments still cost more than one, since a page of receipts is read from a view over all of them, but the receipts are the only thing read of every segment: what was sent in them is read of the segments the page reaches, which is one or two. The smaller records, whose rows have a page or three of receipts, are as quick as before or quicker. The one question slower for it is a span of time over many segments, a few milliseconds where it was one, since a page of receipts in a span is read through the view as every page is.

**Receipts are the one part of the record that grows with every row sent.** A receipt is some 105 to 112 bytes. Runs keep a row once however many answers carried it; receipts keep it once an answer, which is the price of being able to say in order who received it. Where answers repeat a few large lists it is most of the record:

| Record | Before | With receipts | Of which receipts |
| --- | --- | --- | --- |
| Repeated, 1 segment | 2.6 MB | 54 MB | 52 MB |
| Drifting, 1 segment | 20 MB | 72 MB | 52 MB |
| Small, 1 segment | 15 MB | 12 MB | 1.1 MB |
| A million answers, 1 segment | 1.2 GB | 1.8 GB | 0.6 GB |
| A million answers, 24 segments | 4.1 GB | 4.7 GB | 0.6 GB |

The small record reads smaller than before because it was filled again; its receipts are a tenth of it. Filling the million answers took 13 to 19 minutes, where it took 10 to 13.


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
