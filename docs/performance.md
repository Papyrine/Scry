# Performance

What a query costs to write on the server and to read on the client, measured rather than asserted.

Both sides carry two ways of doing the same work. The server can shape a result into dictionaries, serialize that into a `JsonElement`, and serialize the envelope around it a second time — or it can write the rows straight from the projected values into the response buffer. The client can decode a response body to a string and read the payload through a `JsonElement` — or it can read the UTF-8 exactly as it arrived. The pairs produce identical bytes and identical results; what differs is what they spend getting there.

The benchmarks keep both arms of each pair so the difference stays visible and cannot quietly regress.


## Running them

```
dotnet run -c Release --project Benchmarks -- --filter '*'
```

Release is mandatory — BenchmarkDotNet refuses a Debug build. The project is deliberately in no solution; see the note in `CLAUDE.md` for why.

The sources are [`Benchmarks/ResponseBenchmarks.cs`](../Benchmarks/ResponseBenchmarks.cs) and [`Benchmarks/PageBenchmarks.cs`](../Benchmarks/PageBenchmarks.cs) for the server, and [`Benchmarks/ClientReadBenchmarks.cs`](../Benchmarks/ClientReadBenchmarks.cs) for the client. Rows come from an in-memory `[QueryablePoco]` source, so no database is involved and no I/O is being measured.


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

The general path is not dead code: it is what `ScryProcessor.Execute` returns for a transport that is not the HTTP endpoint, and what the endpoint itself falls back to for a result the writer cannot reproduce byte-for-byte. That the two agree exactly is pinned by `FastWriterGoldenTests`.


## Reading a response

| | Rows | Body as a string, payload via `JsonElement` | Body as the UTF-8 it arrived as |
| --- | --- | --- | --- |
| Response | 1000 | 912 KB / 906 µs | **400 KB / 651 µs** |
| Response | 100 | 89 KB / 87 µs | **41 KB / 65 µs** |
| Response | 1 | 2.1 KB / 1.5 µs | **1.4 KB / 1.0 µs** |
| One streamed row | — | 888 B / 978 ns | **384 B / 421 ns** |

The client reads the second way. The first is kept as a measured baseline because it is the shape most transports reach for by default, and because the difference is what justifies `QueryResponse` holding its payload as bytes until something asks for it.

At a thousand rows the read also drops from 143.6 gen-2 collections per 1000 operations to none: the intermediates that used to reach the large object heap are no longer created. That matters most in WebAssembly, where the client usually runs.


## Reading the numbers

**Allocations are the reliable figure.** They reproduce between runs to within a few bytes. Times move with whatever else the machine is doing — two runs of the same build here differed by a third or more on the wall clock (one arm by 43%) while the allocation columns were identical to the byte. Treat the timings as approximate and the ratios as more meaningful than the absolutes.

**Only the fast arm pays HTTP.** It goes through the real endpoint over a loopback round trip; the general-path arm calls the processor directly. So each fast row carries a fixed cost the row beside it does not, which is why at **one row it looks worse** — 2.9× the allocations for a page. Nothing is wrong there; the constant simply dominates. Read the growth from 1 row to 1000 instead, which is what each path actually adds per row:

| Per row | Dictionaries + `JsonElement` + serialize | Written from projected rows | |
| --- | --- | --- | --- |
| List | 2.37 KB / 2.94 µs | 0.85 KB / 0.54 µs | −64% / −82% |
| Page | 2.39 KB / 2.55 µs | 0.86 KB / 0.55 µs | −64% / −79% |

The crossover is around a hundred rows.

**These are one machine's numbers,** taken on Windows 11 with .NET 10.0.11 (x64, RyuJIT AVX2) under BenchmarkDotNet 0.15.2, on a developer machine rather than dedicated hardware. They are here to show the shape of the difference and to make a regression obvious, not as a specification. Re-run them rather than trusting them.


## Where the difference comes from

- **No dictionary per row.** The projection already produces an `object[]` of the requested leaves. The writer walks the projection's shape — member names camel-cased and JSON-escaped once per plan, not once per row — and writes values straight out of that array.
- **No `JsonElement` round trip.** The general path serializes the rows into a document and then serializes the document into the response, so the payload's bytes are produced twice and parsed once in between.
- **No UTF-16 detour.** A request is serialized to UTF-8 and read as UTF-8 on both sides; a response is now the same. Decoding a body to a string transcodes the whole of it for the JSON reader to transcode straight back.
- **Pooled buffers.** The response is written into an array-pool buffer rather than one that doubles from 256 bytes and discards every intermediate on the way up.

See [Wire format](wire-format.md) for what is actually written, and [Server](server.md) for where in the pipeline it happens.
