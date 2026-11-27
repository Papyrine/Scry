import { test } from "node:test";
import assert from "node:assert/strict";
import { createScry, encodeQuery, formFor, gt, not, serializeRequest } from "../src/index.ts";
import type { QueryRequest } from "../src/wire.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import corpus from "../../../src/Scry.Tests/TypeScript/requests.verified.json" with { type: "json" };

const stop = new Error("Stopped before sending.");
let sent: QueryRequest | undefined;

// As a generated C# client sends: the model's members where a query projects nothing, and the stamp.
const scry = createScry<Sources>(schema, {
  transport: {
    query(request) {
      sent = request;
      return Promise.reject(stop);
    }
  }
});

async function capture(run: () => Promise<unknown>): Promise<QueryRequest> {
  sent = undefined;
  try {
    await run();
  } catch (error) {
    if (error !== stop) {
      throw error;
    }
  }

  assert.ok(sent);
  return sent;
}

test("a query projecting nothing asks for the model's scalar members, by name", async () => {
  const request = await capture(() => scry.employee.toList());

  assert.equal(
    serializeRequest(request),
    `{"version":1,"root":"Employee","pipeline":[{"$type":"select","projection":{"members":["Id","Name","Status","Perks","Active","ManagerId","DepartmentId","Avatar"]}}],"stamp":"${schema.stamp}"}`);
});

test("after flattening a collection, the projection is the element's members", async () => {
  const request = await capture(() => scry.order.selectMany(_ => _.lines).toList());

  assert.deepEqual(request.pipeline.at(-1), {
    $type: "select",
    projection: { members: schema.models.OrderLineQueryModel.defaultProjection.map(_ => ({ name: _, value: { $type: "node", node: { $type: "member", path: [_] } } })) }
  });
});

test("a terminal returning no row, and one with its own condition, project nothing", async () => {
  assert.deepEqual((await capture(() => scry.employee.count())).pipeline, [{ $type: "count" }]);
  assert.deepEqual((await capture(() => scry.employee.any())).pipeline, [{ $type: "any" }]);
  assert.deepEqual(
    (await capture(() => scry.employee.first(_ => _.active))).pipeline,
    [{ $type: "first", orDefault: false, predicate: { $type: "member", path: ["Active"] } }]);
});

test("a request names the version it needs and no more", async () => {
  const plain = await capture(() => scry.order.where(_ => gt(_.amount, 1)).toList());
  const richer = await capture(() => scry.order.join(scry.order.orderBy(_ => _.amount).take(1), _ => _.region, _ => _.region, (outer, inner) => ({ code: outer.code, matched: inner.amount })).toList());

  assert.equal(plain.version, 1);
  assert.equal(richer.version, 2);
});

test("JavaScript's own operators on an expression are refused rather than sending something else", () => {
  assert.throws(() => scry.order.where(_ => (_.amount as unknown as number) > 5 as never), /JavaScript's own operators/);
  assert.throws(() => scry.order.select(_ => ({ label: `${_.region as unknown as string}!` as never })), /JavaScript's own operators/);
  assert.throws(() => scry.employee.where(_ => !_.active as never), /use not\(\), and\(\) and or\(\)/);
});

test("a member the model does not have is refused, naming it", () => {
  assert.throws(() => scry.employee.where(_ => (_ as unknown as Record<string, never>)["salary"]!), /'salary' is not a member of Employee/);
});

test("no query reads an attachment", () => {
  assert.throws(() => scry.contract.select(_ => ({ document: (_ as unknown as Record<string, never>)["document"]! })), /attachment, which no query reads/);
});

test("a projected name starting with $ is refused, as the server would", () => {
  assert.throws(() => scry.employee.select(_ => ({ $scry: _.name })), /'\$scry' starts with '\$'/);
});

test("not() negates rather than JavaScript's !", async () => {
  const request = await capture(() => scry.employee.where(_ => not(_.active)).count());
  assert.deepEqual(request.pipeline[0], { $type: "where", predicate: { $type: "unary", op: "Not", operand: { $type: "member", path: ["Active"] } } });
});

test("the URL form is base64url of the request's UTF-8, unpadded", () => {
  for (const shape of corpus) {
    assert.equal(encodeQuery(shape.request), Buffer.from(shape.request, "utf8").toString("base64url"));
  }

  const text = `é 漢 👪 ${String.fromCharCode(0x2028)}`;
  assert.equal(encodeQuery(text), Buffer.from(text, "utf8").toString("base64url"));
});

test("a request travels as a URL where it fits, and as a body where it does not or reads anything sensitive", () => {
  const json = corpus[0]!.request;

  assert.deepEqual(formFor(json, false), { kind: "url", query: encodeQuery(json) });
  assert.deepEqual(formFor(json, true), { kind: "body", body: json });
  assert.deepEqual(formFor(json, false, 10), { kind: "body", body: json });
  assert.deepEqual(formFor(json, false, 0), { kind: "body", body: json });
});
