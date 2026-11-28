// The HTTP transport against answers the server gave, as framings.verified.json records them, and against
// the refusals a hop in front of it gives, over a fetch that answers what each test scripts.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createScry,
  encodeQuery,
  eq,
  gt,
  httpTransport,
  schemaStatus,
  ScryPermissionError,
  ScryRequestError,
  ScryStaleClientError,
  ScryWireError
} from "../src/index.ts";
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

function answer(exchange: Exchange): Response {
  return new Response(exchange.body ?? "", { status: exchange.status, headers: exchange.headers });
}

function client(respond: (call: Call) => Response, options: { onSchemaStale?: (stamps: { client: string; server: string }) => void } = {}) {
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
    transport: httpTransport({ endpoint, fetch, headers: () => ({ Authorization: "Bearer test" }) }),
    ...options
  });
  return { scry, calls };
}

// The query the recorded list exchanges are the answer to.
const listed = (scry: ReturnType<typeof client>["scry"]) =>
  scry.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).toList();

test("a request that fits a URL travels as one, and its rows are read", async () => {
  const exchange = recorded("The same list, as a URL");
  const { scry, calls } = client(() => answer(exchange));

  const rows = await listed(scry);

  assert.deepEqual(rows, [{ name: "Aaron" }, { name: "Alice" }, { name: "Carol" }]);
  assert.equal(calls.length, 1);
  assert.equal(calls[0]!.method, "GET");
  assert.equal(calls[0]!.url, `${endpoint}?q=${encodeQuery(exchange.requestBody!)}`);
  assert.equal(calls[0]!.headers.get("Authorization"), "Bearer test");
});

test("a query reading a sensitive member travels as a body from the start", async () => {
  const exchange = recorded("A list, as a body");
  const { scry, calls } = client(() => answer(exchange));

  await scry.employee.where(_ => eq(_.avatar, new Uint8Array([1, 2, 3]))).select(_ => ({ name: _.name })).toList();

  assert.deepEqual(calls.map(_ => _.method), ["POST"]);
  assert.equal(calls[0]!.headers.get("Content-Type"), "application/json; charset=utf-8");
});

test("a URL the server will only take as a body is asked again as one", async () => {
  const refused = recorded("A sensitive comparison, as a URL");
  const list = recorded("A list, as a body");
  const { scry, calls } = client(_ => answer(_.method === "GET" ? refused : list));

  const rows = await listed(scry);

  assert.deepEqual(calls.map(_ => _.method), ["GET", "POST"]);
  assert.equal(calls[1]!.body, list.requestBody);
  assert.equal(rows.length, 3);
});

test("a URL refused before it reached the server is asked again as a body, and nothing as long asked as a URL again", async () => {
  const list = recorded("A list, as a body");
  const { scry, calls } = client(_ => _.method === "GET" ? new Response("", { status: 414 }) : answer(list));

  await listed(scry);
  await listed(scry);

  assert.deepEqual(calls.map(_ => _.method), ["GET", "POST", "POST"]);
});

test("a 405 says no URL form is mapped, so nothing is asked as a URL again", async () => {
  const list = recorded("A list, as a body");
  const { scry, calls } = client(_ => _.method === "GET" ? new Response("", { status: 405 }) : answer(list));

  await listed(scry);
  await scry.employee.select(_ => ({ name: _.name })).toList();

  assert.deepEqual(calls.map(_ => _.method), ["GET", "POST", "POST"]);
});

test("the URL budget a response advertises bounds what is asked as a URL next", async () => {
  const list = recorded("A list, as a body");
  const advertised = new Response(list.body, { status: 200, headers: { ...list.headers, "Scry-Url-Limit": "10" } });
  const { scry, calls } = client(_ => _.method === "GET" ? advertised : answer(list));

  await listed(scry);
  await listed(scry);

  assert.deepEqual(calls.map(_ => _.method), ["GET", "POST"]);
});

test("a refusal is an error carrying the server's status, code and message", async () => {
  const refusal = recorded("A refusal");
  const { scry } = client(() => answer(refusal));

  await assert.rejects(
    () => scry.order.select(_ => ({ region: _.region })).toList(),
    (error: unknown) =>
      error instanceof ScryRequestError &&
      error.status === 400 &&
      error.code === "Validation" &&
      error.message === "Property 'Revision' is not allow-listed on 'Order'.");
});

test("a refusal the server attributes to a stale client, or to a policy, is told apart", async () => {
  const stale = client(() => new Response(JSON.stringify({ error: "Regenerate.", code: "StaleClient" }), { status: 400 }));
  const denied = client(() => new Response(JSON.stringify({ error: "The query was denied by a server policy.", code: "Forbidden" }), { status: 403 }));

  await assert.rejects(() => stale.scry.employee.count(), ScryStaleClientError);
  await assert.rejects(() => denied.scry.employee.count(), ScryPermissionError);
});

test("a body cut off part way is a failure, never a short answer", async () => {
  const list = recorded("A list, as a body");
  const { scry } = client(() => new Response(list.body!.slice(0, 60), { status: 200 }));

  await assert.rejects(() => listed(scry), (error: unknown) => error instanceof ScryWireError && /ended early/.test(error.message));
});

test("an answer of another kind than the terminal asked for is refused", async () => {
  const list = recorded("A list, as a body");
  const { scry } = client(() => answer(list));

  await assert.rejects(() => scry.employee.count(), (error: unknown) => error instanceof ScryWireError && /Scalar query/.test(error.message));
});

test("a server on another model is told once, and its stamp is known", async () => {
  const list = recorded("A list, as a body");
  const told: { client: string; server: string }[] = [];
  const { scry } = client(() => answer(list), { onSchemaStale: _ => told.push(_) });

  await listed(scry);
  await listed(scry);

  assert.deepEqual(told, [{ client: schema.stamp, server: "{stamp}" }]);
  assert.deepEqual(schemaStatus(scry), { client: schema.stamp, server: "{stamp}", stale: true });
});

test("a refusal's stamp is read too, since drift is when a refusal matters most", async () => {
  const refusal = recorded("A refusal");
  const told: string[] = [];
  const { scry } = client(() => answer(refusal), { onSchemaStale: _ => told.push(_.server) });

  await assert.rejects(() => scry.order.where(_ => gt(_.amount, 1)).count());
  assert.deepEqual(told, ["{stamp}"]);
});
