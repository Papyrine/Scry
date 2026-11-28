// Binary values that travel as multipart parts, and attachments that travel as nothing and are fetched
// by their row's key: read from the exchanges the .NET side recorded.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createBatch, createScry, httpTransport, ScryRequestError, ScryWireError } from "../src/index.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import * as sample from "./generated/sample-model.ts";
import framings from "../../../src/Scry.Tests/TypeScript/framings.verified.json" with { type: "json" };
import recorded from "../../../src/Scry.Tests/TypeScript/binary.verified.json" with { type: "json" };

const endpoint = "https://scry.test/api/query";
const recordings = [...framings, ...recorded];

function exchange(name: string) {
  const found = recordings.find(_ => _.name === name);
  if (found === undefined) {
    throw new Error(`No exchange is recorded as '${name}'.`);
  }

  return found;
}

function bytesOf(name: string): Uint8Array {
  const recording = exchange(name);
  if (recording.bodyBase64 !== undefined) {
    return new Uint8Array(Buffer.from(recording.bodyBase64, "base64"));
  }

  return new TextEncoder().encode(recording.body ?? "");
}

// The recorded answer, its body delivered in pieces of `size` bytes.
function answer(name: string, size = Number.POSITIVE_INFINITY): Response {
  const recording = exchange(name);
  const bytes = bytesOf(name);
  const body = new ReadableStream<Uint8Array>({
    start(controller) {
      for (let at = 0; at < bytes.length; at += size) {
        controller.enqueue(bytes.slice(at, at + size));
      }

      controller.close();
    }
  });
  let delivered: ReadableStream<Uint8Array> | null = body;
  if (recording.status === 204) {
    delivered = null;
  }

  return new Response(delivered, { status: recording.status, headers: recording.headers });
}

interface Seen {
  readonly url: string;
  readonly body: string | undefined;
}

function client<S = Sources>(respond: (seen: Seen, index: number) => Response, described: typeof schema | typeof sample.schema = schema, defaultProjection: "model" | "server" = "server") {
  const seen: Seen[] = [];
  const fetch = async (input: string | URL | Request, init?: RequestInit) => {
    const request = { url: String(input), body: init?.body as string | undefined };
    seen.push(request);
    return respond(request, seen.length - 1);
  };
  const scry = createScry<S>(described, { defaultProjection, stamp: false, transport: httpTransport({ endpoint, fetch }) });
  return { scry, seen };
}

const avatars = (scry: ReturnType<typeof client<Sources>>["scry"]) => scry.employee.orderBy(_ => _.id).take(2).select(_ => ({ name: _.name, avatar: _.avatar }));
const expected = [{ name: "Alice", avatar: new Uint8Array([1, 2, 3]) }, { name: "Carol", avatar: new Uint8Array() }];

test("a binary member that travelled as a part is read from the part its placeholder names", async () => {
  const { scry } = client(() => answer("A binary member, as multipart"));

  assert.deepEqual(await avatars(scry).toList(), expected);
});

test("a multipart body read in pieces reads the same, a delimiter split across them", async () => {
  for (const size of [1, 7, 41]) {
    const { scry } = client(() => answer("A binary member, as multipart", size));

    assert.deepEqual(await avatars(scry).toList(), expected, `in pieces of ${size}`);
  }
});

test("a streamed row's placeholders index the parts that arrived since the row before", async () => {
  const { scry } = client(() => answer("A binary member, streamed", 5));
  const rows = [];
  for await (const row of avatars(scry).stream()) {
    rows.push(row);
  }

  assert.deepEqual(rows, expected);
});

test("a batch's parts are numbered across every entry", async () => {
  const { scry } = client(() => answer("A batch carrying binary parts", 13));
  const batch = createBatch(scry);
  const first = batch.add(_ => avatars(_).toList());
  const names = batch.add(_ => _.employee.where(_ => _.active).orderBy(_ => _.name).select(_ => ({ name: _.name })).toList());
  const third = batch.add(_ => avatars(_).toList());
  await batch.send();

  assert.deepEqual(await first, expected);
  assert.deepEqual(await names, [{ name: "Aaron" }, { name: "Alice" }, { name: "Carol" }]);
  assert.deepEqual(await third, expected);
});

test("a placeholder naming a part the response did not carry fails the read", async () => {
  const text = new TextDecoder().decode(bytesOf("A binary member, as multipart")).replace("{\"$bin\":1}", "{\"$bin\":7}");
  const { scry } = client(() => new Response(text, { headers: exchange("A binary member, as multipart").headers }));

  await assert.rejects(() => avatars(scry).toList(), (error: unknown) => error instanceof ScryWireError && /binary part 7/.test(error.message));
});

test("a part carrying more than it declared, or a body with no JSON part, is not read", async () => {
  const headers = exchange("A binary member, as multipart").headers;
  const text = new TextDecoder().decode(bytesOf("A binary member, as multipart"));
  const long = client(() => new Response(text.replace("Content-Length: 3", "Content-Length: 2"), { headers }));
  const headless = client(() => new Response(text.slice(0, text.indexOf("Content-Type: application/json") - 2) + "--\r\n", { headers }));

  await assert.rejects(() => avatars(long.scry).toList(), /declared 2 bytes and carried 3/);
  await assert.rejects(() => avatars(headless.scry).toList(), /without a JSON part/);
});

// A list of contracts as the server answers one projected by default, keys and all.
const contracts = JSON.stringify({ version: 2, kind: "List", payload: [{ id: 1, name: "Lease", canSealContract: true }, { id: 2, name: "Draft", canSealContract: true }] });

test("a row returned whole carries a handle for each attachment, which fetches the bytes by the row's key", async () => {
  for (const projected of ["model", "server"] as const) {
    const { scry, seen } = client((_, index) => {
      if (index === 0) {
        return new Response(contracts, { headers: { "Content-Type": "application/json" } });
      }

      return answer("An attachment");
    }, schema, projected);

    const [lease] = await scry.contract.toList();
    const content = await lease!.document.open();

    assert.equal(lease!.document.source, "Contract", projected);
    assert.equal(lease!.document.member, "Document");
    assert.equal(seen[1]!.url, `${endpoint}/attachment`);
    assert.equal(seen[1]!.body, exchange("An attachment").requestBody);
    assert.equal(content?.contentType, "application/pdf");
    assert.equal(content?.length, 3);
    assert.deepEqual(await content?.bytes(), new Uint8Array([0x11, 0x22, 0x33]));
  }
});

test("an attachment holding nothing opens as null, and one the server will not hand over throws", async () => {
  const answers = ["An attachment holding nothing", "An attachment the check refuses", "An attachment asked for with no key"];
  const { scry } = client((_, index) => {
    if (index === 0) {
      return new Response(contracts, { headers: { "Content-Type": "application/json" } });
    }

    return answer(answers[index - 1]!);
  });
  const [, draft] = await scry.contract.toList();

  assert.equal(await draft!.document.open(), null);
  await assert.rejects(() => draft!.document.open(), (error: unknown) => error instanceof ScryRequestError && error.status === 404);
  await assert.rejects(() => draft!.document.open(), (error: unknown) => error instanceof ScryRequestError && error.code === "Validation");
});

test("a projection returning an attachment beside its key carries the handle where it was returned", async () => {
  const { scry, seen } = client((_, index) => {
    if (index === 0) {
      return new Response(JSON.stringify({ version: 2, kind: "List", payload: [{ key: 1 }] }), { headers: { "Content-Type": "application/json" } });
    }

    return answer("An attachment");
  });

  const [row] = await scry.contract.select(_ => ({ key: _.id, file: _.document })).toList();
  await row!.file.open();

  assert.deepEqual(Object.keys(row!), ["key", "file"]);
  assert.equal(seen[1]!.body, exchange("An attachment").requestBody);
});

test("an attachment one navigation away is fetched by that row's key, and a row it did not reach carries none", async () => {
  const { scry, seen } = client<sample.Sources>(() => new Response(
    JSON.stringify({ version: 2, kind: "List", payload: [{ name: "Ann", department: { id: 4 } }, { name: "Bo", department: null }] }),
    { headers: { "Content-Type": "application/json" } }), sample.schema);

  const query = scry.employee.select(_ => ({ name: _.name, department: { id: _.department.id, handbook: _.department.handbook } }));
  const [ann, bo] = await query.toList();
  const request = seen[0]!.body ?? Buffer.from(new URL(seen[0]!.url).searchParams.get("q")!, "base64url").toString();

  assert.equal(ann!.department.handbook.source, "Department");
  assert.equal(ann!.department.handbook.member, "Handbook");
  assert.equal(bo!.department, null);
  assert.match(request, /"\$type":"nested","path":"Department","projection":\{"members":\["Id"\]\}/);
  assert.equal(request.includes("Handbook"), false);
});
