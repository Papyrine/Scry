import { test } from "node:test";
import assert from "node:assert/strict";
import { camelCase } from "../src/runtime/naming.ts";
import names from "../../../src/Scry.Tests/TypeScript/names.verified.json" with { type: "json" };

// Every name the server's casing policy was asked about, including the cases where it departs from
// lowering the first letter. A row's keys are written in this casing, so a client that cases a name
// another way reads a member that is not there.
test("casing matches the server's policy for every recorded name", () => {
  for (const [name, written] of Object.entries(names)) {
    assert.equal(camelCase(name), written, name);
  }
});

test("a name already in camel case is left as it is", () => {
  assert.equal(camelCase("name"), "name");
  assert.equal(camelCase(""), "");
});
