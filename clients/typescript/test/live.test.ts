// Live queries: server-sent events as the specification reads them, and the pump's rules for when a
// connection that ends is asked for again, over a transport that answers what each test scripts.

import { test } from "node:test";
import assert from "node:assert/strict";
import { backoff, createScry, ScryRequestError, ScryStaleClientError } from "../src/index.ts";
import type { LiveEvent, Transport } from "../src/index.ts";
import { events, lines } from "../src/http.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import framings from "../../../src/Scry.Tests/TypeScript/framings.verified.json" with { type: "json" };

const recorded = framings.find(_ => _.name === "A live query's first event")!;

async function collect<T>(source: AsyncIterable<T>): Promise<T[]> {
  const out: T[] = [];
  for await (const item of source) {
    out.push(item);
  }

  return out;
}

test("the recorded first event is a result, with its id and the response as its data", async () => {
  const read = await collect(events(lines(new Response(recorded.body))));

  assert.equal(read.length, 1);
  assert.equal(read[0]!.event, "result");
  assert.equal(read[0]!.id, "{fingerprint}");
  assert.match(read[0]!.data, /^\{"version":2,"kind":"List"/);
});

test("events are read as the specification reads them", async () => {
  const text = [
    ": a comment",
    "event: ping",
    "data:",
    "",
    "event: result",
    "id: 7",
    "data: one",
    "data:two",
    "",
    "event: unnamed-without-data",
    "",
    "data: a message",
    "",
    "event: cut"
  ].join("\r\n");

  assert.deepEqual(await collect(events(lines(new Response(text)))), [
    { event: "ping", data: "" },
    { event: "result", id: "7", data: "one\ntwo" },
    { event: "message", data: "a message" }
  ]);
});

// A response with these rows, as the server writes one.
function answer(...names: string[]): string {
  return JSON.stringify({ version: 2, kind: "List", payload: names.map(_ => ({ name: _ })) });
}

const result = (id: string | undefined, ...names: string[]): LiveEvent => ({ event: "result", ...(id === undefined ? {} : { id }), data: answer(...names) });
const end = (reconnect: boolean): LiveEvent => ({ event: "end", data: JSON.stringify({ reconnect }) });
const error = (code: string): LiveEvent => ({ event: "error", data: JSON.stringify({ error: `Refused, ${code}.`, code }) });

interface Opened {
  readonly lastEventId: string | undefined;
  readonly signal: AbortSignal;
}

// Each connection plays the next script: its events, then a failure to throw if it has one.
function scripted(connections: readonly (readonly (LiveEvent | Error)[])[]) {
  const opened: Opened[] = [];
  const transport: Transport = {
    query: () => Promise.reject(new Error("Not asked once.")),
    async *subscribe(_request, context) {
      const script = connections[opened.length];
      opened.push({ lastEventId: context.lastEventId, signal: context.signal });
      if (script === undefined) {
        throw new Error("No more connections were scripted.");
      }

      for (const step of script) {
        if (step instanceof Error) {
          throw step;
        }

        yield step;
      }
    }
  };
  const scry = createScry<Sources>(schema, { defaultProjection: "server", stamp: false, transport, retry: () => 0 });
  return { live: scry.employee.select(_ => ({ name: _.name })).live(), opened };
}

async function first<T>(source: AsyncIterable<T>, count: number): Promise<T[]> {
  const out: T[] = [];
  for await (const item of source) {
    out.push(item);
    if (out.length === count) {
      break;
    }
  }

  return out;
}

test("each answer is the whole current result, and a connection the server ends is asked for again naming the last", async () => {
  const { live, opened } = scripted([
    [result("a", "Alice"), end(true)],
    [{ event: "unchanged", data: "" }, result("b", "Alice", "Bob")]
  ]);

  assert.deepEqual(await first(live, 2), [[{ name: "Alice" }], [{ name: "Alice" }, { name: "Bob" }]]);
  assert.deepEqual(opened.map(_ => _.lastEventId), [undefined, "a"]);
  assert.equal(opened[1]!.signal.aborted, true);
});

test("a connection cut off with no closing event is asked for again", async () => {
  const { live, opened } = scripted([
    [result("a", "Alice")],
    [result("b", "Bob")]
  ]);

  assert.deepEqual(await first(live, 2), [[{ name: "Alice" }], [{ name: "Bob" }]]);
  assert.deepEqual(opened.map(_ => _.lastEventId), [undefined, "a"]);
});

test("a server failing for the moment, or a connection failing, is asked again", async () => {
  const { live, opened } = scripted([
    [error("ExecutionFailed")],
    [new TypeError("fetch failed")],
    [new ScryRequestError(503, "Unknown", "Service Unavailable", false, undefined)],
    [result("a", "Alice")]
  ]);

  assert.deepEqual(await first(live, 1), [[{ name: "Alice" }]]);
  assert.equal(opened.length, 4);
});

test("a rejection, a denial or a stale client ends the live query for good", async () => {
  for (const [code, kind] of [["Validation", ScryRequestError], ["Forbidden", ScryRequestError], ["StaleClient", ScryStaleClientError]] as const) {
    const { live, opened } = scripted([[result("a", "Alice"), error(code)]]);

    await assert.rejects(() => collect(live), kind);
    assert.equal(opened.length, 1, code);
  }
});

test("a server ending it saying not to reconnect ends it", async () => {
  const { live, opened } = scripted([[result("a", "Alice"), end(false)]]);

  assert.deepEqual(await collect(live), [[{ name: "Alice" }]]);
  assert.equal(opened.length, 1);
});

test("a transport naming no answer is not shown the one held again", async () => {
  const { live } = scripted([
    [result(undefined, "Alice"), end(true)],
    [result(undefined, "Alice"), result(undefined, "Bob")]
  ]);

  assert.deepEqual(await first(live, 2), [[{ name: "Alice" }], [{ name: "Bob" }]]);
});

test("a retry policy declining to ask again ends it with the failure", async () => {
  const transport: Transport = {
    query: () => Promise.reject(new Error("Not asked once.")),
    // eslint-disable-next-line require-yield
    async *subscribe() {
      throw new TypeError("fetch failed");
    }
  };
  const scry = createScry<Sources>(schema, { defaultProjection: "server", stamp: false, transport, retry: () => undefined });

  await assert.rejects(() => collect(scry.employee.live()), TypeError);
});

test("a subscription is told every answer until it is stopped", async () => {
  const { live, opened } = scripted([[result("a", "Alice"), result("b", "Bob"), { event: "ping", data: "" }]]);
  const told: unknown[] = [];

  const stop = live.subscribe(_ => told.push(_));
  while (told.length < 2) {
    await new Promise(resolve => setImmediate(resolve));
  }

  stop();
  assert.deepEqual(told, [[{ name: "Alice" }], [{ name: "Bob" }]]);
  assert.equal(opened[0]!.signal.aborted, true);
});

test("the default policy asks at once, then backs off from a second to thirty", () => {
  assert.equal(backoff({ retries: 0, quietFor: 0, failure: undefined }), 0);
  for (const [retries, seconds] of [[1, 1], [2, 2], [3, 4], [6, 30], [20, 30]] as const) {
    const delay = backoff({ retries, quietFor: 0, failure: undefined })!;
    assert.ok(delay >= seconds * 800 && delay <= seconds * 1200, `${retries}: ${delay}`);
  }
});
