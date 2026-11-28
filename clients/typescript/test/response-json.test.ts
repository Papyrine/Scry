import { test } from "node:test";
import assert from "node:assert/strict";
import { parseJson, RawNumber } from "../src/response/json.ts";
import { fromBase64 } from "../src/response/decode.ts";
import { ScryWireError } from "../src/errors.ts";

const backslash = String.fromCharCode(92);

test("a number keeps the text it was written as", () => {
  const parsed = parseJson("{\"long\":18446744073709551615,\"decimal\":100.50,\"double\":1E+21}") as Record<string, RawNumber>;

  assert.equal(parsed["long"]!.text, "18446744073709551615");
  assert.equal(parsed["decimal"]!.text, "100.50");
  assert.equal(parsed["double"]!.text, "1E+21");
});

test("escapes read as the characters they stand for", () => {
  const json = `"${backslash}u00e9${backslash}n${backslash}${backslash}${backslash}"${backslash}uD83D${backslash}uDC6A"`;
  assert.equal(parseJson(json), "é" + String.fromCharCode(10) + backslash + "\"" + String.fromCharCode(0xd83d, 0xdc6a));
});

test("structure reads whole, whitespace around it", () => {
  assert.deepEqual(parseJson(" { \"a\" : [ true , false , null , \"x\" ] , \"b\" : { } } "), { a: [true, false, null, "x"], b: {} });
});

test("a body cut off part way says so", () => {
  for (const cut of ["{\"a\":[1,2", "{\"a\":\"text", "[", "{\"a\""]) {
    assert.throws(() => parseJson(cut), (error: unknown) => error instanceof ScryWireError && /ended early/.test(error.message), cut);
  }
});

test("what is not JSON is refused, naming where", () => {
  assert.throws(() => parseJson("{\"a\":tru}"), ScryWireError);
  assert.throws(() => parseJson("{\"a\":1}x"), /after the end of the value/);
  assert.throws(() => parseJson("{a:1}"), /where a member's name starts/);
});

test("base64 reads back to its bytes", () => {
  assert.deepEqual(fromBase64("AQID"), new Uint8Array([1, 2, 3]));
  assert.deepEqual(fromBase64("/w=="), new Uint8Array([255]));
  assert.deepEqual(fromBase64("//4="), new Uint8Array([255, 254]));
  assert.deepEqual(fromBase64(""), new Uint8Array());
  assert.throws(() => fromBase64("A"), ScryWireError);
});
