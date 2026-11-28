import { test } from "node:test";
import assert from "node:assert/strict";
import { parseDisplay } from "../src/codegen/display.ts";
import type { Known } from "../src/codegen/display.ts";

const known: Known = {
  enums: new Map([["Status", "Status"], ["global::System.DayOfWeek", "DayOfWeek"]]),
  models: new Set(["EmployeeQueryModel"])
};

test("a keyword and a qualified BCL type are scalars", () => {
  assert.deepEqual(parseDisplay("decimal", known), { kind: "value", value: { scalar: "decimal" }, nullable: false });
  assert.deepEqual(parseDisplay("global::System.DateOnly?", known), { kind: "value", value: { scalar: "DateOnly" }, nullable: true });
  assert.deepEqual(parseDisplay("byte[]", known), { kind: "value", value: { scalar: "bytes" }, nullable: false });
});

test("an enum is found by its display, a model's own or the base class library's", () => {
  assert.deepEqual(parseDisplay("Status?", known), { kind: "value", value: { enum: "Status" }, nullable: true });
  assert.deepEqual(parseDisplay("global::System.DayOfWeek", known), { kind: "value", value: { enum: "DayOfWeek" }, nullable: false });
});

test("a model is a navigation, and a list of one a collection of rows", () => {
  assert.deepEqual(parseDisplay("EmployeeQueryModel?", known), { kind: "model", model: "EmployeeQueryModel" });
  assert.deepEqual(
    parseDisplay("global::System.Collections.Generic.IReadOnlyList<EmployeeQueryModel>", known),
    { kind: "list", element: { kind: "model", model: "EmployeeQueryModel" } });
});

test("the attachment handle is an attachment", () => {
  assert.deepEqual(parseDisplay("global::Scry.ScryAttachment", known), { kind: "attachment" });
});

test("a list of a list is refused", () => {
  const list = "global::System.Collections.Generic.IReadOnlyList<";
  assert.throws(() => parseDisplay(`${list}${list}int>>`, known), /a list cannot hold/);
});

test("a name the description does not declare is refused rather than guessed at", () => {
  assert.throws(() => parseDisplay("CustomerQueryModel?", known), /not one this generator knows/);
  assert.throws(() => parseDisplay("global::System.Int128", known), /not one this generator knows/);
});
