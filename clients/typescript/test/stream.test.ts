// A streamed answer, a line at a time: the recorded stream read whole and in pieces, the markers
// required, and a failure after the first row surfaced as the failure it is.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createScry, httpTransport, ScryRequestError, ScryWireError } from "../src/index.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import framings from "../../../src/Scry.Tests/TypeScript/framings.verified.json" with { type: "json" };

const endpoint = "https://scry.test/api/query";
const recorded = framings.find(_ => _.name === "A stream")!;

function body(chunks: readonly string[]): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) {
        controller.enqueue(encoder.encode(chunk));
      }

      controller.close();
    }
  });
}

function client(respond: () => Response) {
  const seen: { url: string; init: RequestInit | undefined }[] = [];
  const fetch = async (input: string | URL | Request, init?: RequestInit) => {
    seen.push({ url: String(input), init });
    return respond();
  };
  const scry = createScry<Sources>(schema, { defaultProjection: "server", stamp: false, transport: httpTransport({ endpoint, fetch }) });
  return { scry, seen };
}

const ndjson = { "Content-Type": "application/x-ndjson" };
const listed = (scry: ReturnType<typeof client>["scry"]) => scry.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).stream();

async function collect<T>(rows: AsyncIterable<T>): Promise<T[]> {
  const out: T[] = [];
  for await (const row of rows) {
    out.push(row);
  }

  return out;
}

test("a stream is sent to its endpoint as a body, and its rows read as they arrive", async () => {
  const { scry, seen } = client(() => new Response(recorded.body, { headers: recorded.headers }));

  assert.deepEqual(await collect(listed(scry)), [{ name: "Aaron" }, { name: "Alice" }, { name: "Carol" }]);
  assert.equal(seen[0]!.url, `${endpoint}/stream`);
  assert.equal(seen[0]!.init?.method, "POST");
  assert.equal(seen[0]!.init?.body, recorded.requestBody);
});

test("lines split across chunks, and across a CR LF, read the same", async () => {
  const text = recorded.body!.replaceAll("\n", "\r\n");
  const pieces = Array.from({ length: Math.ceil(text.length / 7) }, (_, index) => text.slice(index * 7, index * 7 + 7));
  const { scry } = client(() => new Response(body(pieces), { headers: ndjson }));

  assert.deepEqual(await collect(listed(scry)), [{ name: "Aaron" }, { name: "Alice" }, { name: "Carol" }]);
});

test("a failure after the first row arrives as the failure its status would have been", async () => {
  const lines = [
    "{\"$scry\":\"begin\",\"version\":2}",
    "{\"name\":\"Aaron\"}",
    "{\"$scry\":\"error\",\"error\":\"The query returned more than the maximum of 1 streamed rows.\",\"code\":\"Validation\"}",
    ""
  ];
  const { scry } = client(() => new Response(lines.join("\n"), { headers: ndjson }));
  const read: unknown[] = [];

  await assert.rejects(
    async () => {
      for await (const row of listed(scry)) {
        read.push(row);
      }
    },
    (error: unknown) => error instanceof ScryRequestError && error.code === "Validation" && error.status === 400);
  assert.deepEqual(read, [{ name: "Aaron" }]);
});

test("a stream with no closing marker was cut off, and says so", async () => {
  const cut = recorded.body!.replace("{\"$scry\":\"end\"}\n", "");
  const { scry } = client(() => new Response(cut, { headers: ndjson }));

  await assert.rejects(() => collect(listed(scry)), (error: unknown) => error instanceof ScryWireError && /closing marker/.test(error.message));
});

test("a stream of binary parts reads each row with the parts that arrived before it", async () => {
  const streamed = framings.find(_ => _.name === "A binary member, streamed")!;
  const { scry } = client(() => new Response(Buffer.from(streamed.bodyBase64!, "base64"), { headers: streamed.headers }));

  const rows = await collect(scry.employee.orderBy(_ => _.id).take(2).select(_ => ({ name: _.name, avatar: _.avatar })).stream());

  assert.deepEqual(rows, [{ name: "Alice", avatar: new Uint8Array([1, 2, 3]) }, { name: "Carol", avatar: new Uint8Array() }]);
});

test("stopping early ends the connection", async () => {
  const { scry, seen } = client(() => new Response(recorded.body, { headers: recorded.headers }));

  for await (const _ of listed(scry)) {
    break;
  }

  assert.equal(seen[0]!.init?.signal?.aborted, true);
});
