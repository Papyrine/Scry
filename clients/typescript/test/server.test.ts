// Against a running Sample.WebServer, when SCRY_SERVER names it (http://localhost:5249, say):
// the model generated from the server's own introspection, queries sent over HTTP, and the rows the
// server answers read back. Skipped otherwise, since the TypeScript suite needs no server of its own.

import { test } from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { pathToFileURL } from "node:url";
import { and, createBatch, createCommands, createScry, eq, gt, httpTransport, isIn, ne, resultOf, schemaStatus, ScryRequestError, startsWith, toUpper } from "../src/index.ts";
import { readDescription } from "../src/codegen/cli.ts";
import { generate } from "../src/codegen/generate.ts";
import type { Commands, Sources } from "./generated/sample-model.ts";
import * as committed from "./generated/sample-model.ts";

const server = process.env["SCRY_SERVER"];
const skip = server === undefined ? "Set SCRY_SERVER to a running Sample.WebServer to run these." : false;

// The model as this server describes it now, generated and loaded the way an app's build would.
async function live(): Promise<typeof committed> {
  const directory = await mkdtemp(join(tmpdir(), "scry-live-"));
  try {
    const file = join(directory, "model.ts");
    await writeFile(file, generate(await readDescription(`${server}/scry/introspect`), { runtime: "scry-client", from: "the live server" }));
    return await import(pathToFileURL(file).href) as typeof committed;
  } finally {
    await rm(directory, { recursive: true, force: true });
  }
}

const methods: string[] = [];

async function client() {
  const { schema } = await live();
  const fetch: typeof globalThis.fetch = (input, init) => {
    methods.push(init?.method ?? "GET");
    return globalThis.fetch(input, init);
  };
  return createScry<Sources>(schema, { transport: httpTransport({ endpoint: `${server}/api/query`, fetch }) });
}

test("rows a query projecting nothing returns read as the model's", { skip }, async () => {
  const scry = await client();
  const rows = await scry.employee.orderBy(_ => _.id).toList();

  assert.ok(rows.length > 0);
  const [first] = rows;
  assert.equal(typeof first!.id, "number");
  assert.equal(typeof first!.name, "string");
  assert.equal(typeof first!.active, "boolean");
  assert.match(first!.created, /^\d{4}-\d{2}-\d{2}$/);
  assert.deepEqual(schemaStatus(scry).stale, false);
});

test("a filtered, ordered projection through a navigation", { skip }, async () => {
  const scry = await client();
  const rows = await scry.employee
    .where(_ => and(_.active, startsWith(_.name, "A")))
    .orderBy(_ => _.name)
    .select(_ => ({ name: _.name, manager: _.manager.name, department: { name: toUpper(_.department.name) } }))
    .toList();

  assert.ok(rows.length > 0);
  for (const row of rows) {
    assert.ok(row.name.startsWith("A"), row.name);
    assert.equal(row.department.name, row.department.name?.toUpperCase());
  }
});

test("scalars, a grouping and a page", { skip }, async () => {
  const scry = await client();
  const count = await scry.employee.count();
  const totals = await scry.order.groupBy(_ => _.region).select(g => ({ region: g.key, total: g.sum(_ => _.amount), orders: g.count() })).toList();
  const first = await scry.employee.orderBy(_ => _.id).select(_ => ({ id: _.id })).page(2);

  assert.equal(typeof count, "number");
  assert.ok(count > 0);
  assert.ok(totals.length > 0);
  for (const total of totals) {
    assert.match(total.total, /^\d+(\.\d+)?$/);
  }

  assert.equal(first.items.length, Math.min(2, count));
  if (first.hasMore) {
    assert.equal(typeof first.cursor, "string");
    const next = await scry.employee.orderBy(_ => _.id).select(_ => ({ id: _.id })).page(2, first.cursor);
    assert.ok(next.items.every(_ => _.id > first.items.at(-1)!.id));
  }
});

test("a short query travels as a URL, and one comparing a sensitive member as a body", { skip }, async () => {
  const scry = await client();
  methods.length = 0;
  await scry.order.where(_ => _.tags.contains("urgent")).select(_ => ({ id: _.id })).toList();
  await scry.employee.where(_ => ne(_.password, "guess")).select(_ => ({ id: _.id })).toList();

  assert.deepEqual(methods, ["GET", "POST"]);
});

test("a value looked up in another source, and the rows a join returns", { skip }, async () => {
  const scry = await client();
  const inDepartment = await scry.employee.where(_ => isIn(_.departmentId, scry.department.where(_ => gt(_.id, 0)), _ => _.id)).count();
  const joined = await scry.employee.join(scry.department, _ => _.departmentId, _ => _.id, (employee, department) => ({ employee: employee.name, department: department.name })).toList();

  assert.ok(inDepartment > 0);
  assert.ok(joined.length > 0);
  assert.ok(joined.every(_ => typeof _.department === "string"));
});

test("a client generated against another model hears of it once", { skip }, async () => {
  const told: string[] = [];
  const scry = createScry<Sources>(committed.schema, {
    transport: httpTransport({ endpoint: `${server}/api/query` }),
    onSchemaStale: _ => told.push(_.server)
  });

  await scry.employee.count();
  await scry.employee.where(_ => eq(_.active, true)).count();

  assert.equal(told.length, 1);
  assert.equal(schemaStatus(scry).stale, true);
});

test("a stream's rows are the rows the same query lists", { skip }, async () => {
  const scry = await client();
  const listed = await scry.employee.orderBy(_ => _.id).select(_ => ({ id: _.id, name: _.name })).toList();
  const streamed = [];
  for await (const row of scry.employee.orderBy(_ => _.id).select(_ => ({ id: _.id, name: _.name })).stream()) {
    streamed.push(row);
  }

  assert.deepEqual(streamed, listed);
});

test("a batch answers each of its queries as it would have been answered alone", { skip }, async () => {
  const scry = await client();
  const batch = createBatch(scry);
  const count = batch.add(_ => _.employee.count());
  const names = batch.add(_ => _.employee.orderBy(_ => _.id).select(_ => ({ name: _.name })).toList());
  methods.length = 0;
  await batch.send();

  assert.deepEqual(methods, ["POST"]);
  assert.equal(await count, await scry.employee.count());
  assert.deepEqual(await names, await scry.employee.orderBy(_ => _.id).select(_ => ({ name: _.name })).toList());
});

test("a live query answers again when what it read is written", { skip, timeout: 60_000 }, async () => {
  const scry = await client();
  const answers = [];
  for await (const answer of scry.order.orderBy(_ => _.id).select(_ => ({ id: _.id, amount: _.amount })).live()) {
    answers.push(answer);
    if (answers.length === 1) {
      const written = await fetch(`${server}/api/orders/reprice-bulk`, { method: "POST" });
      assert.equal(written.status, 204);
      continue;
    }

    break;
  }

  const [before, after] = answers;
  assert.ok(before!.length > 0);
  assert.deepEqual(after!.map(_ => _.id), before!.map(_ => _.id));
  assert.equal(Number(after![0]!.amount), Number(before![0]!.amount) + 1);
});

test("a command is decided at once, and its result read typed", { skip }, async () => {
  const scry = await client();
  const commands = createCommands<Commands>(scry);

  const hired = await commands.send.createEmployee({ name: "Typed Hire", departmentId: 1, status: "FullTime" });
  const { id } = resultOf(hired);
  const row = await scry.employee.where(_ => eq(_.id, id)).select(_ => ({ name: _.name })).single();

  assert.equal(hired.status, "Completed");
  assert.equal(row.name, "Typed Hire");
});

test("a command the server's validation refuses throws", { skip }, async () => {
  const commands = createCommands<Commands>(await client());

  await assert.rejects(
    () => commands.send.createEmployee({ name: "", departmentId: 1, status: "FullTime" }),
    (error: unknown) => error instanceof ScryRequestError && error.code === "Validation");
});

test("a slow command goes pending, and its outcome follows", { skip, timeout: 60_000 }, async () => {
  const scry = await client();
  const commands = createCommands<Commands>(scry, { wait: 500 });
  const { id } = await scry.employee.orderBy(_ => _.id).select(_ => ({ id: _.id })).first();

  const renaming = await commands.send.renameEmployee({ id, name: "A slow rename" });

  assert.equal(renaming.status, "Pending");
  assert.deepEqual(commands.pending.map(_ => ({ command: _.command, keys: _.keys })), [{ command: "RenameEmployee", keys: [id] }]);
  assert.equal((await renaming.completion).status, "Completed");
  assert.equal((await scry.employee.where(_ => eq(_.id, id)).select(_ => ({ name: _.name })).single()).name, "A slow rename");
});

test("what a caller may send, command by command and row by row", { skip }, async () => {
  const scry = await client();
  const commands = createCommands<Commands>(scry);
  await commands.ready;
  const rows = await scry.employee.select(_ => ({ active: _.active, deletable: _.canDeleteEmployee })).toList();

  assert.equal(commands.can("createEmployee"), true);
  assert.ok(rows.some(_ => _.deletable === false));
  assert.ok(rows.every(_ => !_.deletable || !_.active));
});

test("a binary member travels as parts and reads back as its bytes, listed or streamed", { skip }, async () => {
  const scry = await client();
  const logos = scry.department.orderBy(_ => _.id).select(_ => ({ id: _.id, logo: _.logo }));
  const listed = await logos.toList();
  const streamed = [];
  for await (const row of logos.stream()) {
    streamed.push(row);
  }

  assert.ok(listed.some(_ => _.logo instanceof Uint8Array && _.logo.length > 0));
  assert.deepEqual(streamed, listed);
});

test("each row carries a handle that fetches its attachment, or opens as null where it holds none", { skip }, async () => {
  const scry = await client();
  const employees = await scry.employee.orderBy(_ => _.id).toList();
  const photos = await Promise.all(employees.map(_ => _.photo.open()));
  const shown = photos.filter(_ => _ !== null);

  assert.ok(shown.length > 0);
  assert.ok(photos.some(_ => _ === null));
  assert.equal(shown[0]!.contentType, "image/svg+xml");
  assert.match(new TextDecoder().decode(await shown[0]!.bytes()), /<svg/);
});

test("an attachment one navigation away is fetched by that row's key", { skip }, async () => {
  const scry = await client();
  const [row] = await scry.employee.orderBy(_ => _.id).select(_ => ({ name: _.name, department: { id: _.department.id, handbook: _.department.handbook } })).toList();
  const handbook = await row!.department.handbook.open();

  assert.equal(handbook === null || handbook.contentType.startsWith("text/plain"), true);
});
