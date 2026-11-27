import { test } from "node:test";
import assert from "node:assert/strict";
import { quote, serializeRequest } from "../src/request/json.ts";
import type { QueryRequest } from "../src/wire.ts";
import corpus from "../../../src/Scry.Tests/TypeScript/requests.verified.json" with { type: "json" };

// The members of every object reversed, so a request is written in the wire's order whatever order it
// was built in.
function reversed(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map(reversed);
  }

  if (value !== null &&
      typeof value === "object") {
    return Object.fromEntries(Object.entries(value).reverse().map(([key, member]) => [key, reversed(member)]));
  }

  return value;
}

test("every request in the corpus is written back byte for byte", () => {
  for (const shape of corpus) {
    const request = reversed(JSON.parse(shape.request)) as QueryRequest;
    assert.equal(serializeRequest(request), shape.request, shape.csharp);
  }
});

test("a path of one segment, and a member reading its own name, each have one spelling", () => {
  const request: QueryRequest = {
    version: 1,
    root: "Employee",
    pipeline: [
      { $type: "where", predicate: { $type: "member", path: ["Manager", "Name"] } },
      {
        $type: "select",
        projection: {
          members: [
            { name: "Name", value: { $type: "node", node: { $type: "member", path: ["Name"] } } },
            { name: "Boss", value: { $type: "node", node: { $type: "member", path: ["Manager", "Name"] } } }
          ]
        }
      }
    ]
  };

  assert.equal(
    serializeRequest(request),
    "{\"version\":1,\"root\":\"Employee\",\"pipeline\":[{\"$type\":\"where\",\"predicate\":{\"$type\":\"member\",\"path\":[\"Manager\",\"Name\"]}},{\"$type\":\"select\",\"projection\":{\"members\":[\"Name\",{\"name\":\"Boss\",\"value\":{\"$type\":\"node\",\"node\":{\"$type\":\"member\",\"path\":[\"Manager\",\"Name\"]}}}]}}]}");
});

// What System.Text.Json's default encoder writes, as the .NET side printed it for every ASCII
// character and for the edges past it. Each escape is built from its code point here.
const backslash = String.fromCharCode(92);
const u = (code: number) => `${backslash}u${code.toString(16).toUpperCase().padStart(4, "0")}`;
const range = (from: number, to: number) => Array.from({ length: to - from }, (_, offset) => u(from + offset)).join("");

test("text is escaped as the server's encoder escapes it", () => {
  const ascii = Array.from({ length: 128 }, (_, code) => String.fromCharCode(code)).join("");
  const expected =
    "\"" + range(0x00, 0x08) +
    `${backslash}b${backslash}t${backslash}n` + u(0x0b) + `${backslash}f${backslash}r` + range(0x0e, 0x20) +
    " !" + u(0x22) + "#$%" + u(0x26) + u(0x27) + "()*" + u(0x2b) + ",-./0123456789:;" + u(0x3c) + "=" + u(0x3e) +
    "?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[" + backslash + backslash + "]^_" + u(0x60) +
    "abcdefghijklmnopqrstuvwxyz{|}~" + u(0x7f) + "\"";

  assert.equal(quote(ascii), expected);
});

test("past ASCII every unit is escaped, a pair as two and a lone surrogate as the replacement", () => {
  const text = String.fromCharCode(0x7f, 0x80, 0xa0, 0x2028, 0xd83d, 0xde00, 0xd800) + "x";
  assert.equal(quote(text), "\"" + u(0x7f) + u(0x80) + u(0xa0) + u(0x2028) + u(0xd83d) + u(0xde00) + u(0xfffd) + "x\"");
});
