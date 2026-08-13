# Benchmarks

What the plan-driven response writer is worth: rows written straight from the projected values — for one query, a page, or every entry of a batch — against the dictionary-and-`JsonElement` path a non-HTTP transport still takes.

```bash
dotnet run -c Release --project Benchmarks -- --filter '*'
dotnet run -c Release --project Benchmarks -- --list flat
```

Release is mandatory (BenchmarkDotNet refuses a Debug build), and this is a project rather than a solution — see `CLAUDE.md` for why a `.slnx` here breaks the `src` projects' packaging.


## Method

The source is a `[QueryablePoco]` supplied in memory, so no database is involved: the benchmark needs real rows to shape and serialize, both arms pay the same provider overhead, and the difference between them is exactly the shaping and serialization under test.

`Legacy` is `ScryProcessor.Execute` plus `ScryJson.Serialize` — a dictionary per row, a `JsonElement` payload, then the envelope serialized a second time. `Endpoint` is the HTTP endpoint, which writes rows straight from the projected values. The two produce byte-identical output, pinned by `FastWriterGoldenTests` in `IntegrationTests`.

`BatchBenchmarks` is the same pair for a batch, where the general path pays that per entry and then serializes every one of those elements again as the envelope. It holds the rows at a hundred and varies the entry count instead, since what a batch adds is the per-entry work repeated.

`TerminalBenchmarks` is the same pair for the results that are not rows — one projected row, and a count. A terminal costs the same whatever the source holds, so it carries them as batch entries to divide out the fixed cost each arm pays, and keeps the source narrow so the pipeline work both arms share does not bury the difference.


## Reading the output

Only the endpoint arm pays HTTP framing and a loopback round trip, so its absolute totals carry a constant the other does not. Compare the **marginal** cost instead — the growth from 1 row to 1000, which subtracts each arm's own fixed cost and leaves what each actually adds per row, in both time and allocation.

Expect the endpoint arm to *lose* at one row, where the measurement is almost entirely that transport constant, and to win by a wide margin by a thousand. A run where it does not is worth investigating before trusting any other number in the table.

The allocation column deserves as much attention as the time column: at a thousand rows the difference is on the order of a megabyte per response, which is GC pressure rather than a one-off cost.
