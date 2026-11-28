// A batch: queries collected, sent as one request, and each answered or refused on its own.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createBatch, createScry, httpTransport, ScryRequestError } from "../src/index.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import framings from "../../../src/Scry.Tests/TypeScript/framings.verified.json" with { type: "json" };

const endpoint = "https://scry.test/api/query";
const recorded = framings.find(_ => _.name === "A batch, one entry refused")!;

function client(respond: () => Response) {
  const seen: { url: string; body: unknown }[] = [];
  const fetch = async (input: string | URL | Request, init?: RequestInit) => {
    seen.push({ url: String(input), body: init?.body });
    return respond();
  };
  const scry = createScry<Sources>(schema, { defaultProjection: "server", stamp: false, transport: httpTransport({ endpoint, fetch }) });
  return { scry, seen };
}

test("queries collected in a batch are sent as one request, each answered or refused on its own", async () => {
  const { scry, seen } = client(() => new Response(recorded.body, { headers: recorded.headers }));
  const batch = createBatch(scry);

  const rows = batch.add(_ => _.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).toList());
  const refused = batch.add(_ => _.order.select(_ => ({ region: _.region })).toList());
  await batch.send();

  assert.deepEqual(await rows, [{ name: "Aaron" }, { name: "Alice" }, { name: "Carol" }]);
  await assert.rejects(refused, (error: unknown) => error instanceof ScryRequestError && error.code === "Validation" && error.status === 400);
  assert.equal(seen.length, 1);
  assert.equal(seen[0]!.url, `${endpoint}/batch`);
  assert.match(String(seen[0]!.body), /^\{"version":2,"queries":\[\{"version":1,"root":"Employee",/);
});

test("a batch that fails to send fails every entry", async () => {
  const { scry } = client(() => new Response("", { status: 502 }));
  const batch = createBatch(scry);
  const count = batch.add(_ => _.employee.count());

  await assert.rejects(() => batch.send(), ScryRequestError);
  await assert.rejects(count, ScryRequestError);
});

test("a batch is used once, and an entry is one query's terminal", async () => {
  const { scry } = client(() => new Response(recorded.body, { headers: recorded.headers }));
  const batch = createBatch(scry);

  assert.throws(() => batch.add(() => Promise.resolve(1)), /one query ended by its terminal/);
  await batch.send();
  assert.throws(() => batch.add(_ => _.employee.count()), /has been sent/);
  await assert.rejects(() => batch.send(), /sent already/);
});
