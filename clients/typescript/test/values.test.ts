import { test } from "node:test";
import assert from "node:assert/strict";
import { base64, constant, formatDouble } from "../src/request/values.ts";
import type { EnumInfo, ValueInfo } from "../src/runtime/schema.ts";

const perks: EnumInfo = { values: ["None", "Parking", "Gym", "Remote"], constants: ["0", "1", "2", "4"], flags: true, underlying: "int" };
const day: EnumInfo = {
  values: ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
  constants: ["0", "1", "2", "3", "4", "5", "6"],
  flags: false,
  underlying: "int",
  system: "System.DayOfWeek"
};
const enums = (name: string) => ({ Perks: perks, DayOfWeek: day })[name];

function spelled(value: Parameters<typeof constant>[0], type: ValueInfo, position: "operand" | "argument" = "operand") {
  const node = constant(value, type, position, enums);
  return [node.value, node.tag];
}

// What .NET's invariant double.ToString printed for each, on the .NET side.
test("a double is spelled as .NET spells it", () => {
  const printed: [number, string][] = [
    [1e15, "1000000000000000"],
    [1e14, "100000000000000"],
    [123456789012345678, "1.2345678901234568E+17"],
    [0.0001, "0.0001"],
    [0.00001, "1E-05"],
    [1.5, "1.5"],
    [-0, "-0"],
    [Number.NaN, "NaN"],
    [Number.POSITIVE_INFINITY, "Infinity"],
    [Number.NEGATIVE_INFINITY, "-Infinity"],
    [1e-7, "1E-07"],
    [5e-324, "5E-324"],
    [1.7976931348623157e308, "1.7976931348623157E+308"],
    [0.1 + 0.2, "0.30000000000000004"],
    [100, "100"],
    [1234567.891, "1234567.891"],
    [2.5e-5, "2.5E-05"],
    [1e16, "10000000000000000"],
    [999999999999999, "999999999999999"]
  ];

  for (const [value, expected] of printed) {
    assert.equal(formatDouble(value), expected, String(value));
  }
});

test("a decimal keeps the scale its text was written with, and a number is written out in full", () => {
  assert.deepEqual(spelled("100.50", { scalar: "decimal" }), ["100.50", "Decimal"]);
  assert.deepEqual(spelled(100.5, { scalar: "decimal" }), ["100.5", "Decimal"]);
  assert.deepEqual(spelled(1e21, { scalar: "decimal" }), ["1000000000000000000000", "Decimal"]);
  assert.deepEqual(spelled(0.0000001, { scalar: "decimal" }), ["0.0000001", "Decimal"]);
});

test("beside an operator, an integer narrower than an int, and a char, is one", () => {
  assert.deepEqual(spelled("A", { scalar: "char" }), ["65", "Int32"]);
  assert.deepEqual(spelled(5, { scalar: "ushort" }), ["5", "Int32"]);
  assert.deepEqual(spelled(1.5, { scalar: "int" }), ["1.5", "Double"]);
  assert.deepEqual(spelled(3000000000, { scalar: "int" }), ["3000000000", "Int64"]);
  assert.deepEqual(spelled(5000000000n, { scalar: "int" }), ["5000000000", "Int64"]);
});

test("as an argument, a value keeps the member's own type", () => {
  assert.deepEqual(spelled("A", { scalar: "char" }, "argument"), ["A", "String"]);
  assert.deepEqual(spelled(5, { scalar: "short" }, "argument"), ["5", "Int32"]);
  assert.deepEqual(spelled(5, { scalar: "ushort" }, "argument"), ["5", "String"]);
});

test("a uint and a ulong travel as text, and a negative literal beside a uint as a long", () => {
  assert.deepEqual(spelled(7, { scalar: "uint" }), ["7", "String"]);
  assert.deepEqual(spelled(-1, { scalar: "uint" }), ["-1", "Int64"]);
  assert.deepEqual(spelled(18446744073709551615n, { scalar: "ulong" }), ["18446744073709551615", "String"]);
});

test("a temporal value is written in its round-trip form, whatever part of it was written", () => {
  assert.deepEqual(spelled("2026-09-03", { scalar: "DateTime" }), ["2026-09-03T00:00:00.0000000", "DateTime"]);
  assert.deepEqual(spelled("2026-09-03T05:06:07.123Z", { scalar: "DateTime" }), ["2026-09-03T05:06:07.1230000Z", "DateTime"]);
  assert.deepEqual(spelled(new Date(Date.UTC(2026, 8, 3, 5, 6, 7, 123)), { scalar: "DateTime" }), ["2026-09-03T05:06:07.1230000Z", "DateTime"]);
  assert.deepEqual(spelled("2026-09-03", { scalar: "DateOnly" }), ["2026-09-03", "DateOnly"]);
  assert.deepEqual(spelled("05:06", { scalar: "TimeOnly" }), ["05:06:00.0000000", "String"]);
  assert.deepEqual(spelled("2026-03-04T05:06:07Z", { scalar: "DateTimeOffset" }), ["2026-03-04T05:06:07.0000000+00:00", "String"]);
  assert.deepEqual(spelled("2026-03-04T05:06:07.123+02:00", { scalar: "DateTimeOffset" }), ["2026-03-04T05:06:07.1230000+02:00", "String"]);
});

// As .NET's TimeSpan.ToString printed each.
test("an elapsed time is written as TimeSpan writes it, the fraction only where there is one", () => {
  assert.deepEqual(spelled("1:02:03", { scalar: "TimeSpan" }), ["01:02:03", "String"]);
  assert.deepEqual(spelled("2.01:02:03.456", { scalar: "TimeSpan" }), ["2.01:02:03.4560000", "String"]);
  assert.deepEqual(spelled("-00:00:00.0000001", { scalar: "TimeSpan" }), ["-00:00:00.0000001", "String"]);
});

test("a Guid is written in lowercase, without braces", () => {
  assert.deepEqual(spelled("{5F2B8A3C-1D4E-4F60-9A7B-8C9D0E1F2A3B}", { scalar: "Guid" }), ["5f2b8a3c-1d4e-4f60-9a7b-8c9d0e1f2a3b", "Guid"]);
});

test("a [Flags] value is written as Enum.ToString writes it, lowest first", () => {
  assert.deepEqual(spelled(["Gym", "Parking"], { enum: "Perks" }), ["Parking, Gym", "Enum"]);
  assert.deepEqual(spelled("Remote, Parking, Gym", { enum: "Perks" }), ["Parking, Gym, Remote", "Enum"]);
  assert.deepEqual(spelled([], { enum: "Perks" }), ["None", "Enum"]);
});

test("System.DayOfWeek alone travels as its number", () => {
  assert.deepEqual(spelled("Monday", { enum: "DayOfWeek" }), ["1", "Int32"]);
});

test("a value of another type than the member's is refused rather than sent", () => {
  assert.throws(() => spelled("yes", { scalar: "bool" }), /not a value of a member typed 'bool'/);
  assert.throws(() => spelled("Sometimes", { enum: "Perks" }), /'Sometimes' is not a Perks/);
  assert.throws(() => spelled("2026-13", { scalar: "DateOnly" }), /not a date/);
});

test("bytes are base64, padded", () => {
  assert.equal(base64(new Uint8Array([1, 2, 3])), "AQID");
  assert.equal(base64(new Uint8Array([255])), "/w==");
  assert.equal(base64(new Uint8Array([255, 254])), "//4=");
  assert.equal(base64(new Uint8Array()), "");
});
