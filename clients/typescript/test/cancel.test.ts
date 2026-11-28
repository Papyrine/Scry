// A terminal's signal: handed to the transport without changing the request, abandoning a request in
// flight as the reason it was aborted for, ending a stream part way as that reason rather than as a
// stream cut off, and let go of once a stream is done with it.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createServer, type ServerResponse } from "node:http";
import type { AddressInfo } from "node:net";
import { getEventListeners } from "node:events";
import { createBatch, createScry, httpTransport, type QueryOptions, type Scry } from "../src/index.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import framings from "../../../src/Scry.Tests/TypeScript/framings.verified.json" with { type: "json" };

const endpoint = "https://scry.test/api/query";
const recordedStream = framings.find(_ => _.name === "A stream")!;

interface Sent {
  readonly url: string;
  readonly body: unknown;
  readonly signal: AbortSignal | null | undefined;
}

// Records what each fetch was asked, then fails it: what is sent is all these tests read.
class Unsent extends Error {}

function recording() {
  const sent: Sent[] = [];
  const fetch = async (input: string | URL | Request, init?: RequestInit) => {
    sent.push({ url: String(input), body: init?.body, signal: init?.signal });
    throw new Unsent();
  };
  const scry = createScry<Sources>(schema, { defaultProjection: "server", stamp: false, transport: httpTransport({ endpoint, fetch }) });
  return { scry, sent };
}

type Terminal = (scry: Scry<Sources>, options?: QueryOptions) => Promise<unknown>;

const terminals: Readonly<Record<string, Terminal>> = {
  toList: (scry, options) => scry.employee.toList(options),
  first: (scry, options) => scry.employee.first(options),
  "first with a condition": (scry, options) => scry.employee.first(_ => _.active, options),
  firstOrDefault: (scry, options) => scry.employee.firstOrDefault(_ => _.active, options),
  single: (scry, options) => scry.employee.single(_ => _.active, options),
  singleOrDefault: (scry, options) => scry.employee.singleOrDefault(options),
  last: (scry, options) => scry.employee.orderBy(_ => _.id).last(options),
  lastOrDefault: (scry, options) => scry.employee.orderBy(_ => _.id).lastOrDefault(_ => _.active, options),
  elementAt: (scry, options) => scry.employee.orderBy(_ => _.id).elementAt(2, options),
  count: (scry, options) => scry.employee.count(options),
  "count with a condition": (scry, options) => scry.employee.count(_ => _.active, options),
  longCount: (scry, options) => scry.employee.longCount(_ => _.active, options),
  any: (scry, options) => scry.employee.any(options),
  all: (scry, options) => scry.employee.all(_ => _.active, options),
  sum: (scry, options) => scry.employee.sum(_ => _.id, options),
  average: (scry, options) => scry.employee.average(_ => _.id, options),
  min: (scry, options) => scry.employee.min(_ => _.id, options),
  max: (scry, options) => scry.employee.max(_ => _.id, options),
  minBy: (scry, options) => scry.employee.minBy(_ => _.id, options),
  maxBy: (scry, options) => scry.employee.maxBy(_ => _.id, options),
  page: (scry, options) => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).page(options),
  "page of a size": (scry, options) => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).page(2, options),
  "page after a cursor": (scry, options) => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).page(2, "eyJrIjpbXX0.c2ln", options),
  "a projection's list": (scry, options) => scry.employee.select(_ => ({ name: _.name })).toList(options)
};

for (const [name, terminal] of Object.entries(terminals)) {
  test(`${name}: the signal reaches the transport, and the request is the one sent without it`, async () => {
    const { scry, sent } = recording();
    const { signal } = new AbortController();

    await assert.rejects(terminal(scry), Unsent);
    await assert.rejects(terminal(scry, { signal }), Unsent);
    await assert.rejects(terminal(scry, {}), Unsent);

    assert.equal(sent.length, 3);
    assert.equal(sent[0]!.signal, undefined);
    assert.equal(sent[1]!.signal, signal);
    assert.equal(sent[2]!.signal, undefined);
    for (const later of sent.slice(1)) {
      assert.equal(later.url, sent[0]!.url);
      assert.equal(later.body, sent[0]!.body);
    }
  });
}

test("the options stand in for the size, and for the cursor, and page() with none asks for the server's size", async () => {
  const { scry, sent } = recording();
  const { signal } = new AbortController();
  const query = scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name }));

  await assert.rejects(query.page({ signal }), Unsent);
  await assert.rejects(query.page(), Unsent);
  await assert.rejects(query.page(2, { signal }), Unsent);
  await assert.rejects(query.page(2), Unsent);
  await assert.rejects(query.page(undefined, "eyJrIjpbXX0.c2ln", { signal }), Unsent);
  await assert.rejects(query.page(undefined, "eyJrIjpbXX0.c2ln"), Unsent);

  assert.equal(sent[0]!.url, sent[1]!.url);
  assert.equal(sent[2]!.url, sent[3]!.url);
  assert.equal(sent[4]!.url, sent[5]!.url);
  assert.notEqual(sent[0]!.url, sent[2]!.url);
  assert.notEqual(sent[2]!.url, sent[4]!.url);
});

// A server on a port of its own that takes each request and never answers it, until told to: what
// a request abandoned in flight needs, over the real fetch.
async function holding(answer?: (response: ServerResponse) => void) {
  const arrived: (() => void)[] = [];
  const abandoned: (() => void)[] = [];
  let requests = 0;
  const server = createServer((_request, response) => {
    requests++;
    response.on("close", () => {
      if (!response.writableEnded) {
        abandoned.shift()?.();
      }
    });
    answer?.(response);
    arrived.shift()?.();
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  const scry = createScry<Sources>(schema, {
    defaultProjection: "server",
    stamp: false,
    transport: httpTransport({ endpoint: `http://127.0.0.1:${port}/api/query` })
  });
  return {
    scry,
    get requests() {
      return requests;
    },
    arrival: () => new Promise<void>(resolve => arrived.push(resolve)),
    abandonment: () => new Promise<void>(resolve => abandoned.push(resolve)),
    async [Symbol.asyncDispose]() {
      server.closeAllConnections();
      await new Promise(resolve => server.close(resolve));
    }
  };
}

test("a request in flight is abandoned when its signal aborts, and the terminal rejects with the reason", async () => {
  await using server = await holding();
  const controller = new AbortController();
  const reason = new Error("No longer wanted.");
  const arrived = server.arrival();
  const abandoned = server.abandonment();

  const counted = server.scry.employee.count(_ => _.active, { signal: controller.signal });
  await arrived;
  controller.abort(reason);

  await assert.rejects(counted, error => error === reason);
  await abandoned;
});

test("a signal already aborted sends nothing", async () => {
  await using server = await holding();
  const controller = new AbortController();
  controller.abort();

  await assert.rejects(server.scry.employee.toList({ signal: controller.signal }), { name: "AbortError" });
  assert.equal(server.requests, 0);
});

// The recorded stream's opening marker and first row, and nothing after: a stream still running.
const [opening, firstRow] = recordedStream.body!.split("\n");

test("a stream aborted part way ends with the reason, not as a stream the server cut off", async () => {
  await using server = await holding(response => {
    response.writeHead(200, { "Content-Type": "application/x-ndjson" });
    response.write(`${opening}\n${firstRow}\n`);
  });
  const controller = new AbortController();
  const reason = new Error("Enough rows.");
  const abandoned = server.abandonment();
  const rows = server.scry.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).stream({ signal: controller.signal });
  const reading = rows[Symbol.asyncIterator]();

  assert.deepEqual(await reading.next(), { done: false, value: { name: "Aaron" } });
  controller.abort(reason);

  await assert.rejects(reading.next(), error => error === reason);
  await abandoned;
});

test("a stream waiting on its next row ends with the reason as soon as the signal aborts", async () => {
  await using server = await holding(response => {
    response.writeHead(200, { "Content-Type": "application/x-ndjson" });
    response.write(`${opening}\n`);
  });
  const controller = new AbortController();
  const reason = new Error("Enough waiting.");
  const arrived = server.arrival();
  const rows = server.scry.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).stream({ signal: controller.signal });
  const next = rows[Symbol.asyncIterator]().next();

  await arrived;
  controller.abort(reason);

  await assert.rejects(next, error => error === reason);
});

test("a stream lets go of its signal once done with it, read to the end or stopped early", async () => {
  const fetch = async () => new Response(recordedStream.body, { headers: recordedStream.headers });
  const scry = createScry<Sources>(schema, { defaultProjection: "server", stamp: false, transport: httpTransport({ endpoint, fetch }) });
  const listed = (signal: AbortSignal) => scry.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).stream({ signal });
  const lifetime = new AbortController();

  for await (const _ of listed(lifetime.signal)) {
    // Read to the end.
  }

  for await (const _ of listed(lifetime.signal)) {
    break;
  }

  assert.equal(getEventListeners(lifetime.signal, "abort").length, 0);
});

test("a query in a batch goes with the batch: send's signal abandons it, and each entry rejects with the reason", async () => {
  await using server = await holding();
  const controller = new AbortController();
  const reason = new Error("Not now.");
  const arrived = server.arrival();
  const batch = createBatch(server.scry);
  const listed = batch.add(_ => _.employee.toList());
  const counted = batch.add(_ => _.employee.count());

  const sending = batch.send(controller.signal);
  await arrived;
  controller.abort(reason);

  await assert.rejects(sending, error => error === reason);
  await assert.rejects(listed, error => error === reason);
  await assert.rejects(counted, error => error === reason);
});
