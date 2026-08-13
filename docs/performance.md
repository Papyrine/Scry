# Performance

What a query costs to write on the server and to read on the client, measured rather than asserted.

Both sides carry two ways of doing the same work. The server can shape a result into dictionaries, serialize that into a `JsonElement`, and serialize the envelope around it a second time — or it can write the rows straight from the projected values into the response buffer, for one query or for every entry of a batch. The client can decode a response body to a string and read the payload through a `JsonElement` — or it can read the UTF-8 exactly as it arrived. The pairs produce identical bytes and identical results; what differs is what they spend getting there.

The HTTP endpoint and the typed client take the second of each pair. The benchmarks keep both arms so the difference stays visible and cannot quietly regress.


## Running them

```
dotnet run -c Release --project Benchmarks -- --filter '*'
```

Release is mandatory — BenchmarkDotNet refuses a Debug build. The project is deliberately in no solution; see the note in `CLAUDE.md` for why.

The sources are [`Benchmarks/ResponseBenchmarks.cs`](../Benchmarks/ResponseBenchmarks.cs), [`Benchmarks/PageBenchmarks.cs`](../Benchmarks/PageBenchmarks.cs) and [`Benchmarks/BatchBenchmarks.cs`](../Benchmarks/BatchBenchmarks.cs) for the server, and [`Benchmarks/ClientReadBenchmarks.cs`](../Benchmarks/ClientReadBenchmarks.cs) for the client. Rows come from an in-memory `[QueryablePoco]` source, so the measurement is shaping and serialization with no database and no I/O in it.


## Writing a response

Nine scalar members per row, which is a wide enough row that shaping and serialization dominate.

| Result | Rows | Dictionaries + `JsonElement` + serialize | Written from projected rows |
| --- | --- | --- | --- |
| List | 1000 | 2390 KB / 3455 µs | **913 KB / 1443 µs** |
| List | 100 | 254 KB / 643 µs | **148 KB / 948 µs** |
| List | 1 | 20 KB / 514 µs | 66 KB / 900 µs |
| Page | 1000 | 2410 KB / 3332 µs | **931 KB / 1717 µs** |
| Page | 100 | 259 KB / 902 µs | **153 KB / 1209 µs** |
| Page | 1 | 23 KB / 785 µs | 69 KB / 1170 µs |

A page costs what a list costs — 931 KB against 913 KB at a thousand rows — because it is the same rows through the same writer, with the `items`/`hasMore`/`cursor` envelope written around them.

The general path serves every transport that is not the HTTP endpoint, which is what `ScryProcessor.Execute` returns, and the endpoint itself falls back to it for a result the writer cannot reproduce byte-for-byte. `FastWriterGoldenTests` pins that the two agree exactly.


## Writing a batch

A batch is the same work repeated, so the entry count is what varies here and the rows per entry sit at a hundred.

| Entries | Rows each | Dictionaries + `JsonElement` + serialize | Written from projected rows |
| --- | --- | --- | --- |
| 1 | 100 | 255 KB / 1023 µs | **150 KB** / 1541 µs |
| 5 | 100 | 1270 KB / 5198 µs | **590 KB / 4812 µs** |
| 20 | 100 | 5077 KB / 20273 µs | **2235 KB / 18321 µs** |

Per entry, taken as the growth from 1 entry to 20, the general path costs 254 KB and 1.01 ms and the writer costs 110 KB and 0.88 ms — **−57%** of the allocations and −13% of the time.

The allocation figure is the one to read. A hundred rows is about where the single-response crossover sits, so at that width the two arms are close on the clock by construction; what separates them is that the general path builds a dictionary per row and a `JsonElement` per entry, and the envelope then serializes every one of those elements a second time. The batch endpoint writes each entry's rows into the envelope as it goes, so the payload's bytes are produced once.

A batch also pays HTTP once for however many entries it carries, which is why the endpoint arm goes from 1.5× the wall clock at one entry to 0.90× at twenty: the constant that dominates a single small response is divided across the batch.


## Reading a response

| | Rows | Body as a string, payload via `JsonElement` | Body as the UTF-8 it arrived as |
| --- | --- | --- | --- |
| Response | 1000 | 912 KB / 906 µs | **400 KB / 651 µs** |
| Response | 100 | 89 KB / 87 µs | **41 KB / 65 µs** |
| Response | 1 | 2.1 KB / 1.5 µs | **1.4 KB / 1.0 µs** |
| One streamed row | — | 888 B / 978 ns | **384 B / 421 ns** |

The client reads the bytes as they arrived. The string-and-`JsonElement` arm is measured beside it because that is the shape most transports reach for by default, and because the gap between them is what `QueryResponse` holding its payload as bytes until something asks for it buys.

At a thousand rows the reading arm makes no gen-2 collections at all, against 143.6 per 1000 operations for the other: nothing on that path is large enough or long-lived enough to reach the large object heap. That matters most in WebAssembly, where the client usually runs.


## Reading the numbers

**Allocations are the reliable figure.** They reproduce between runs to within a few bytes. Times move with whatever else the machine is doing — two runs of the same build here differed by a third or more on the wall clock (one arm by 43%) while the allocation columns were identical to the byte. Treat the timings as approximate and the ratios as more meaningful than the absolutes.

**Only the fast arm pays HTTP.** It goes through the real endpoint over a loopback round trip; the general-path arm calls the processor directly. So each fast row carries a fixed cost the row beside it does not, which is why at **one row it looks worse** — 2.9× the allocations for a page. Nothing is wrong there; the constant dominates. Read the growth from 1 row to 1000 instead, which is what each path adds per row:

| Per row | Dictionaries + `JsonElement` + serialize | Written from projected rows | |
| --- | --- | --- | --- |
| List | 2.37 KB / 2.94 µs | 0.85 KB / 0.54 µs | −64% / −82% |
| Page | 2.39 KB / 2.55 µs | 0.86 KB / 0.55 µs | −64% / −79% |

The crossover is around a hundred rows.

**These are one machine's numbers,** taken on Windows 11 with .NET 10.0.11 (x64, RyuJIT AVX2) under BenchmarkDotNet 0.15.2, on a developer machine rather than dedicated hardware. They are here to show the shape of the difference and to make a regression obvious, not as a specification. Re-run them rather than trusting them.


## Where the difference comes from

- **The writer walks the projection's shape.** The projection produces an `object[]` of the requested leaves, and the writer walks a name tree built once per plan — member names camel-cased and JSON-escaped at that point rather than per row — writing values straight out of that array.
- **The payload's bytes are produced once.** The writer emits the complete envelope, version through stamp, as it goes. The general path serializes rows into a document and then serializes that document into the response, so the same bytes are produced twice with a parse in between; a batch pays that per entry, and its envelope is the second pass over all of them at once.
- **UTF-8 end to end.** A request and a response are both serialized to UTF-8 and read as UTF-8 on both sides. Decoding a body to a string transcodes the whole of it for the JSON reader to transcode straight back.
- **Pooled buffers.** A response is written into an array-pool buffer, which hands each intermediate back to the pool as it grows. A buffer that doubles from 256 bytes instead discards every intermediate on the way up, and the last of those are large enough to land on the large object heap.

See [Wire format](wire-format.md) for what is actually written, and [Server](server.md) for where in the pipeline it happens.
