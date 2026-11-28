// What the server answered each corpus shape with, over the seeded test database, read back through
// the TypeScript query that sends the shape: every answer has to read, as the types the query says.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createScry } from "../src/index.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import { apart, corpusCases } from "./corpus-cases.ts";
import corpus from "../../../src/Scry.Tests/TypeScript/requests.verified.json" with { type: "json" };

let answer = "";

const scry = createScry<Sources>(schema, {
  defaultProjection: "server",
  stamp: false,
  transport: {
    query: () => Promise.resolve({ text: answer })
  }
});
const cases = corpusCases(scry);

const answered = corpus.filter(_ => "response" in _ && !Object.hasOwn(apart, _.csharp)) as { csharp: string; response: string }[];

function read(label: string): Promise<unknown> {
  const shape = answered.find(_ => _.csharp === label);
  assert.ok(shape, label);
  answer = shape.response;
  return cases[label]!();
}

for (const shape of answered) {
  test(`reads ${shape.csharp}`, async () => {
    answer = shape.response;
    await cases[shape.csharp]!();
  });
}

test("a decimal keeps the digits the server wrote, as text", async () => {
  const rows = await read(`_ => _.Source<Order>("Order").Where(_ => _.Amount >= 100m && _.Amount < 250m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`) as { region: string; amount: string }[];

  assert.ok(rows.length > 0);
  for (const row of rows) {
    assert.equal(typeof row.amount, "string");
    assert.match(row.amount, /^\d+(\.\d+)?$/);
  }
});

test("a count is a number, a long count a bigint, and any a boolean", async () => {
  assert.equal(typeof await read(`_ => _.Source<Employee>("Employee").CountAsync()`), "number");
  assert.equal(typeof await read(`_ => _.Source<Employee>("Employee").LongCountAsync()`), "bigint");
  assert.equal(typeof await read(`_ => _.Source<Employee>("Employee").AnyAsync()`), "boolean");
});

test("a row is keyed by the names the query wrote", async () => {
  const rows = await read(`_ => _.Source<Employee>("Employee").Where(_ => _.Name != escapes).Select(_ => new {Größe = _.Name}).ToListAsync()`) as Record<string, unknown>[];

  assert.ok(rows.length > 0);
  assert.deepEqual(Object.keys(rows[0]!), ["größe"]);
});

test("a nested object and a returned collection read as the query built them", async () => {
  const nested = await read(`_ => _.Source<Employee>("Employee").Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name))).ToListAsync()`) as { name: string; department: { name: string } }[];
  const lines = await read(`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Lines = _.Lines.Where(l => l.Quantity > 1).Select(l => new {l.Sku, l.Quantity}).ToList()}).ToListAsync()`) as { region: string; lines: { sku: string; quantity: number }[] }[];
  const prices = await read(`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Prices = _.Lines.Select(l => l.Price * 2m).ToList()}).ToListAsync()`) as { prices: string[] }[];

  assert.equal(typeof nested[0]!.department.name, "string");
  assert.ok(lines.some(_ => _.lines.length > 0));
  for (const line of lines.flatMap(_ => _.lines)) {
    assert.equal(typeof line.quantity, "number");
  }

  assert.ok(prices.flatMap(_ => _.prices).every(_ => typeof _ === "string"));
});

test("a page carries its rows, whether there are more, and the cursor to ask for them with", async () => {
  const page = await read(`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToPageAsync(2)`) as { items: { name: string }[]; hasMore: boolean; cursor?: string };

  assert.equal(page.items.length, 2);
  assert.equal(page.hasMore, true);
  assert.equal(typeof page.cursor, "string");
});

test("an enum is read by name, the day of the week as .NET names it", async () => {
  const rows = await read(`_ => _.Source<Order>("Order").GroupBy(_ => _.Placed.DayOfWeek).Select(_ => new {Day = _.Key, Count = _.Count()}).ToListAsync()`) as { day: string; count: number }[];
  const days = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];

  assert.ok(rows.length > 0);
  for (const row of rows) {
    assert.ok(days.includes(row.day), row.day);
    assert.equal(typeof row.count, "number");
  }
});
