// A query's own headers, as the C# client's WithHeader, WithHeaders and OnResponseHeaders: sent with its
// request alone and never in it, written once however many times the request is sent, read off the
// response that answered, a refusal's included, and refused in a batch, which is one request.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createBatch, createScry, httpTransport, ScryRequestError, type LiveEvent, type Transport } from "../src/index.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import framings from "../../../src/Scry.Tests/TypeScript/framings.verified.json" with { type: "json" };

const endpoint = "https://scry.test/api/query";

interface Call {
  readonly method: string;
  readonly url: string;
  readonly body: string | undefined;
  readonly headers: Headers;
}

type Exchange = (typeof framings)[number];

function recorded(name: string): Exchange {
  const exchange = framings.find(_ => _.name === name);
  assert.ok(exchange, name);
  return exchange;
}

// The recorded answer, with any headers given besides.
function answer(exchange: Exchange, extra: Record<string, string> = {}): Response {
  return new Response(exchange.body ?? "", { status: exchange.status, headers: { ...exchange.headers, ...extra } });
}

function client(respond: (call: Call) => Response) {
  const calls: Call[] = [];
  const fetch = async (input: string | URL | Request, init?: RequestInit) => {
    const call: Call = {
      method: init?.method ?? "GET",
      url: String(input),
      body: typeof init?.body === "string" ? init.body : undefined,
      headers: new Headers(init?.headers)
    };
    calls.push(call);
    return respond(call);
  };
  const scry = createScry<Sources>(schema, {
    defaultProjection: "server",
    stamp: false,
    transport: httpTransport({ endpoint, fetch, headers: { Authorization: "Bearer test", "X-Build": "transport" } })
  });
  return { scry, calls };
}

// The query the recorded list exchanges are the answer to.
const listed = (scry: ReturnType<typeof client>["scry"]) =>
  scry.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name }));

test("a query's own header travels with its request alone, beside the transport's, and leaves the request as it was", async () => {
  const exchange = recorded("The same list, as a URL");
  const { scry, calls } = client(() => answer(exchange));

  await listed(scry).withHeader("X-Correlation", "one").toList();
  await listed(scry).toList();

  assert.equal(calls[0]!.headers.get("X-Correlation"), "one");
  assert.equal(calls[0]!.headers.get("Authorization"), "Bearer test");
  assert.equal(calls[1]!.headers.get("X-Correlation"), null);
  assert.equal(calls[0]!.url, calls[1]!.url);
});

test("a query's header is written over the transport's of the same name, whatever its case", async () => {
  const exchange = recorded("The same list, as a URL");
  const { scry, calls } = client(() => answer(exchange));

  await listed(scry).withHeader("x-build", "query").toList();

  assert.equal(calls[0]!.headers.get("X-Build"), "query");
});

test("where a header is written in the chain makes no difference, and a second composes with the first", async () => {
  const exchange = recorded("The same list, as a URL");
  const { scry, calls } = client(() => answer(exchange));

  await scry.employee
    .withHeader("X-First", "1")
    .where(_ => _.active)
    .orderBy(_ => _.name)
    .withHeaders(_ => _.set("X-Second", "2"))
    .select(_ => ({ name: _.name }))
    .toList();

  assert.equal(calls[0]!.headers.get("X-First"), "1");
  assert.equal(calls[0]!.headers.get("X-Second"), "2");
  await listed(scry).toList();
  assert.equal(calls[0]!.url, calls[1]!.url);
});

test("a request asked again as a body carries the headers written once, and the response that answered is read", async () => {
  const list = recorded("A list, as a body");
  const { scry, calls } = client(_ => {
    if (_.method === "GET") {
      return new Response("", { status: 414, headers: { "X-Trace": "refused" } });
    }

    return answer(list, { "X-Trace": "answered" });
  });
  let written = 0;
  const read: (string | null)[] = [];

  await listed(scry)
    .withHeaders(_ => _.set("X-Attempt", String(++written)))
    .onResponseHeaders(_ => read.push(_.get("X-Trace")))
    .toList();

  assert.deepEqual(calls.map(_ => _.method), ["GET", "POST"]);
  assert.deepEqual(calls.map(_ => _.headers.get("X-Attempt")), ["1", "1"]);
  assert.equal(written, 1);
  assert.deepEqual(read, ["answered"]);
});

test("a refusal's headers are read before it is thrown", async () => {
  const refusal = recorded("A refusal");
  const { scry } = client(() => answer(refusal, { "X-Trace": "why" }));
  const read: (string | null)[] = [];

  await assert.rejects(
    scry.employee.onResponseHeaders(_ => read.push(_.get("X-Trace"))).select(_ => ({ name: _.name })).toList(),
    ScryRequestError);

  assert.deepEqual(read, ["why"]);
});

test("a stream carries them, and reads its response's before the first row", async () => {
  const stream = recorded("A stream");
  const { scry, calls } = client(() => answer(stream, { "X-Trace": "streamed" }));
  const seen: string[] = [];

  for await (const row of listed(scry).withHeader("X-Correlation", "two").onResponseHeaders(_ => seen.push(`headers ${_.get("X-Trace")}`)).stream()) {
    seen.push(row.name);
  }

  assert.equal(calls[0]!.headers.get("X-Correlation"), "two");
  assert.deepEqual(seen, ["headers streamed", "Aaron", "Alice", "Carol"]);
});

test("a live query writes them afresh for every connection", async () => {
  const sent: (string | null)[] = [];
  const result = (id: string, ...names: string[]): LiveEvent => ({
    event: "result",
    id,
    data: JSON.stringify({ version: 2, kind: "List", payload: names.map(_ => ({ name: _ })) })
  });
  const connections = [[result("a", "Alice"), { event: "end", data: JSON.stringify({ reconnect: true }) }], [result("b", "Bob")]];
  const transport: Transport = {
    query: () => Promise.reject(new Error("Not asked once.")),
    async *subscribe(_request, context) {
      sent.push(context.headers?.get("X-Connection") ?? null);
      yield* connections[sent.length - 1]!;
    }
  };
  const scry = createScry<Sources>(schema, { defaultProjection: "server", stamp: false, transport, retry: () => 0 });
  let written = 0;
  const answers: unknown[] = [];

  for await (const answer of scry.employee.withHeaders(_ => _.set("X-Connection", String(++written))).select(_ => ({ name: _.name })).live()) {
    answers.push(answer);
    if (answers.length === 2) {
      break;
    }
  }

  assert.deepEqual(answers, [[{ name: "Alice" }], [{ name: "Bob" }]]);
  assert.deepEqual(sent, ["1", "2"]);
});

test("a query in a batch cannot carry headers of its own, and the batch keeps the others", async () => {
  const exchange = recorded("A batch, one entry refused");
  const { scry, calls } = client(() => answer(exchange));
  const batch = createBatch(scry);

  const rows = batch.add(_ => _.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).toList());
  assert.throws(() => batch.add(_ => _.employee.withHeader("X-Correlation", "three").count()), /cannot carry headers of its own/);
  assert.throws(() => batch.add(_ => _.employee.onResponseHeaders(() => {}).count()), /cannot carry headers of its own/);
  const refused = batch.add(_ => _.order.select(_ => ({ region: _.region })).toList());
  await batch.send();

  assert.equal(calls.length, 1);
  assert.deepEqual(await rows, [{ name: "Aaron" }, { name: "Alice" }, { name: "Carol" }]);
  await assert.rejects(refused, ScryRequestError);
});
