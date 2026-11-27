// A constant as it travels: its invariant text and the tag of the shape the client had. The server
// parses the text into the member's own type, so the tag is a hint; it is matched here only so that a
// request is the byte-string the C# client sends for the same query. Mirrors ValueTag.Of in Scry.Client,
// and the spellings each .NET type's invariant ToString gives.

import type { ClrTypeTag, ConstNode } from "../wire.ts";
import type { EnumInfo, Scalar, ValueInfo } from "../runtime/schema.ts";

/** A value a query may hold as a constant. */
export type Literal = string | number | bigint | boolean | Date | Uint8Array | readonly string[] | null;

/**
 * Where a constant stands, which decides the type C# would have given it. Beside an operator it takes
 * the type the operator works in, where every integer narrower than an int, and a char, is widened to
 * one; as a method's argument, or in a list of values, it keeps the member's own type.
 */
export type Position = "operand" | "argument";

export interface EnumLookup {
  (name: string): EnumInfo | undefined;
}

const int32 = { min: -2147483648, max: 2147483647 };

export function constant(value: Literal, type: ValueInfo | undefined, position: Position, enums: EnumLookup): ConstNode {
  if (value === null) {
    return { $type: "const", tag: "Null" };
  }

  const [text, tag] = spell(value, type, position, enums);
  return { $type: "const", value: text, tag };
}

function spell(value: Exclude<Literal, null>, type: ValueInfo | undefined, position: Position, enums: EnumLookup): [string, ClrTypeTag] {
  if (type === undefined) {
    return inferred(value);
  }

  if ("enum" in type) {
    const info = enums(type.enum);
    if (info === undefined) {
      throw new Error(`The enum '${type.enum}' is not described.`);
    }

    return enumeration(value, type.enum, info);
  }

  return scalar(value, type.scalar, position);
}

// A constant with nothing beside it to type it, as the value C# would have held.
function inferred(value: Exclude<Literal, null>): [string, ClrTypeTag] {
  if (typeof value === "string") {
    return [value, "String"];
  }

  if (typeof value === "boolean") {
    return [boolean(value), "Boolean"];
  }

  if (typeof value === "bigint") {
    return [value.toString(), "Int64"];
  }

  if (typeof value === "number") {
    if (Number.isInteger(value) &&
        value >= int32.min &&
        value <= int32.max) {
      return [String(value), "Int32"];
    }

    return [formatDouble(value), "Double"];
  }

  if (value instanceof Uint8Array) {
    return [base64(value), "Bytes"];
  }

  if (value instanceof Date) {
    return [dateTimeOf(value), "DateTime"];
  }

  throw new Error("A list of names is a value only of a [Flags] enum.");
}

function scalar(value: Exclude<Literal, null>, type: Scalar, position: Position): [string, ClrTypeTag] {
  switch (type) {
    case "string":
      return [text(value, type), "String"];
    case "char": {
      const character = text(value, type);
      if (character.length !== 1) {
        throw new Error(`A char is one UTF-16 unit, and '${character}' is ${character.length}.`);
      }

      // An operator widens a char to the int it is, so the constant arrives as its code.
      if (position === "operand") {
        return [String(character.charCodeAt(0)), "Int32"];
      }

      return [character, "String"];
    }
    case "bool":
      if (typeof value !== "boolean") {
        throw mismatch(value, type);
      }

      return [boolean(value), "Boolean"];
    case "sbyte":
    case "byte":
    case "short":
    case "ushort":
    case "int":
      return integral(value, type, position);
    case "uint":
      return unsigned(value, type, 0xffffffff, position);
    case "long":
      return wide(value, type);
    case "ulong":
      return [whole(value, type).toString(), "String"];
    case "float":
    case "double":
      return [double(value, type), "Double"];
    case "decimal":
      return [decimal(value), "Decimal"];
    case "DateTime":
      return [dateTime(value), "DateTime"];
    case "DateOnly":
      return [dateOnly(value), "DateOnly"];
    // Neither has a tag of its own, so both travel as text, in the round-trip form that carries the
    // whole value.
    case "TimeOnly":
      return [timeOnly(value), "String"];
    case "DateTimeOffset":
      return [dateTimeOffset(value), "String"];
    // No round-trip form is asked for, so the constant form a TimeSpan's ToString gives.
    case "TimeSpan":
      return [timeSpan(value), "String"];
    case "Guid":
      return [guid(value), "Guid"];
    case "bytes":
      if (!(value instanceof Uint8Array)) {
        throw mismatch(value, type);
      }

      return [base64(value), "Bytes"];
  }
}

// An integer narrower than a long. Beside an operator C# widens it to an int, and a literal that is
// not one becomes what it is: a long past an int, a double with a fraction.
function integral(value: Exclude<Literal, null>, type: Scalar, position: Position): [string, ClrTypeTag] {
  if (typeof value === "bigint") {
    return [value.toString(), "Int64"];
  }

  if (typeof value !== "number") {
    throw mismatch(value, type);
  }

  if (position === "operand") {
    if (!Number.isInteger(value)) {
      return [formatDouble(value), "Double"];
    }

    if (value < int32.min ||
        value > int32.max) {
      return [String(value), "Int64"];
    }

    return [String(value), "Int32"];
  }

  if (!Number.isInteger(value)) {
    throw mismatch(value, type);
  }

  // ValueTag.Of has a case for short and byte; sbyte and ushort fall to their invariant text.
  if (type === "sbyte" ||
      type === "ushort") {
    return [String(value), "String"];
  }

  return [String(value), "Int32"];
}

function unsigned(value: Exclude<Literal, null>, type: Scalar, max: number, position: Position): [string, ClrTypeTag] {
  if (typeof value === "number" &&
      position === "operand" &&
      !Number.isInteger(value)) {
    return [formatDouble(value), "Double"];
  }

  const number = whole(value, type);
  // Beside an operator, a negative literal and a uint meet as longs.
  if (position === "operand" &&
      (number < 0n || number > BigInt(max))) {
    return [number.toString(), "Int64"];
  }

  return [number.toString(), "String"];
}

function wide(value: Exclude<Literal, null>, type: Scalar): [string, ClrTypeTag] {
  if (typeof value === "number" &&
      !Number.isInteger(value)) {
    return [formatDouble(value), "Double"];
  }

  return [whole(value, type).toString(), "Int64"];
}

function whole(value: Exclude<Literal, null>, type: Scalar): bigint {
  if (typeof value === "bigint") {
    return value;
  }

  if (typeof value === "number" &&
      Number.isSafeInteger(value)) {
    return BigInt(value);
  }

  if (typeof value === "string" &&
      /^-?\d+$/.test(value)) {
    return BigInt(value);
  }

  throw mismatch(value, type);
}

function double(value: Exclude<Literal, null>, type: Scalar): string {
  if (typeof value === "number") {
    return formatDouble(value);
  }

  if (typeof value === "bigint") {
    return formatDouble(Number(value));
  }

  throw mismatch(value, type);
}

function text(value: Exclude<Literal, null>, type: string): string {
  if (typeof value !== "string") {
    throw mismatch(value, type);
  }

  return value;
}

function boolean(value: boolean): string {
  if (value) {
    return "true";
  }

  return "false";
}

function mismatch(value: unknown, type: string): Error {
  let shown = String(value);
  if (typeof value === "string") {
    shown = JSON.stringify(value);
  }

  return new Error(`${shown} is not a value of a member typed '${type}'.`);
}

/**
 * A double as .NET's invariant ToString writes it: the shortest digits that read back as the same
 * value, in full unless the exponent is past 16 or below -4, where it is written as `1E+17` or
 * `1E-05`.
 */
export function formatDouble(value: number): string {
  if (Number.isNaN(value)) {
    return "NaN";
  }

  if (value === Number.POSITIVE_INFINITY) {
    return "Infinity";
  }

  if (value === Number.NEGATIVE_INFINITY) {
    return "-Infinity";
  }

  if (value === 0) {
    if (Object.is(value, -0)) {
      return "-0";
    }

    return "0";
  }

  const { negative, digits, exponent } = decompose(value);
  let sign = "";
  if (negative) {
    sign = "-";
  }

  if (exponent > 16 ||
      exponent < -4) {
    let mantissa = digits.charAt(0);
    if (digits.length > 1) {
      mantissa += `.${digits.slice(1)}`;
    }

    let exponentSign = "+";
    if (exponent < 0) {
      exponentSign = "-";
    }

    return `${sign}${mantissa}E${exponentSign}${String(Math.abs(exponent)).padStart(2, "0")}`;
  }

  return sign + fixed(digits, exponent);
}

// The shortest digits that identify a number, and where its point goes.
function decompose(value: number): { negative: boolean; digits: string; exponent: number } {
  const [mantissa, power] = value.toExponential().split("e") as [string, string];
  return {
    negative: mantissa.startsWith("-"),
    digits: mantissa.replace("-", "").replace(".", ""),
    exponent: Number(power)
  };
}

function fixed(digits: string, exponent: number): string {
  if (exponent < 0) {
    return `0.${"0".repeat(-exponent - 1)}${digits}`;
  }

  const whole = digits.slice(0, exponent + 1).padEnd(exponent + 1, "0");
  const fraction = digits.slice(exponent + 1);
  if (fraction.length === 0) {
    return whole;
  }

  return `${whole}.${fraction}`;
}

/**
 * A decimal's invariant text. Text is taken as it is, scale and all (`100.50` stays `100.50`, as a
 * C# literal would); a number is written out in full, never with an exponent.
 */
function decimal(value: Exclude<Literal, null>): string {
  if (typeof value === "string") {
    if (!/^-?\d+(\.\d+)?$/.test(value)) {
      throw new Error(`'${value}' is not a decimal's text.`);
    }

    return value;
  }

  if (typeof value === "bigint") {
    return value.toString();
  }

  if (typeof value === "number") {
    if (!Number.isFinite(value)) {
      throw new Error(`${value} is not a decimal.`);
    }

    if (value === 0) {
      return "0";
    }

    const { negative, digits, exponent } = decompose(value);
    let sign = "";
    if (negative) {
      sign = "-";
    }

    return sign + fixed(digits, exponent);
  }

  throw mismatch(value, "decimal");
}

const dateTimePattern = /^(\d{4})-(\d{2})-(\d{2})(?:[T ](\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?)?(Z)?$/;

// A wall clock, with the Z that marks a UTC one, as DateTime's round-trip form writes it.
function dateTime(value: Exclude<Literal, null>): string {
  if (value instanceof Date) {
    return dateTimeOf(value);
  }

  const parts = dateTimePattern.exec(text(value, "DateTime"));
  if (parts === null) {
    throw new Error(`'${String(value)}' is not a date and time: write it as 2026-09-03T05:06:07.1230000, with a Z for UTC.`);
  }

  const [, year, month, day, hour, minute, second, fraction, utc] = parts;
  return `${year}-${month}-${day}T${hour ?? "00"}:${minute ?? "00"}:${second ?? "00"}.${(fraction ?? "").padEnd(7, "0")}${utc ?? ""}`;
}

// A JavaScript Date is an instant, so it is the UTC wall clock of that instant.
function dateTimeOf(value: Date): string {
  return `${datePart(value)}T${timePart(value)}Z`;
}

function datePart(value: Date): string {
  const year = String(value.getUTCFullYear()).padStart(4, "0");
  const month = String(value.getUTCMonth() + 1).padStart(2, "0");
  const day = String(value.getUTCDate()).padStart(2, "0");
  return `${year}-${month}-${day}`;
}

function timePart(value: Date): string {
  const hours = String(value.getUTCHours()).padStart(2, "0");
  const minutes = String(value.getUTCMinutes()).padStart(2, "0");
  const seconds = String(value.getUTCSeconds()).padStart(2, "0");
  const fraction = String(value.getUTCMilliseconds()).padStart(3, "0").padEnd(7, "0");
  return `${hours}:${minutes}:${seconds}.${fraction}`;
}

function dateOnly(value: Exclude<Literal, null>): string {
  if (value instanceof Date) {
    return datePart(value);
  }

  const written = text(value, "DateOnly");
  if (!/^\d{4}-\d{2}-\d{2}$/.test(written)) {
    throw new Error(`'${written}' is not a date: write it as 2026-09-03.`);
  }

  return written;
}

const timePattern = /^(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?$/;

function timeOnly(value: Exclude<Literal, null>): string {
  const parts = timePattern.exec(text(value, "TimeOnly"));
  if (parts === null) {
    throw new Error(`'${String(value)}' is not a time of day: write it as 05:06:07.1230000.`);
  }

  const [, hour, minute, second, fraction] = parts;
  return `${hour}:${minute}:${second ?? "00"}.${(fraction ?? "").padEnd(7, "0")}`;
}

const offsetPattern = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?(Z|[+-]\d{2}:\d{2})$/;

// The offset is part of the value and is kept, with a zero one written +00:00 as .NET writes it.
function dateTimeOffset(value: Exclude<Literal, null>): string {
  if (value instanceof Date) {
    return `${datePart(value)}T${timePart(value)}+00:00`;
  }

  const parts = offsetPattern.exec(text(value, "DateTimeOffset"));
  if (parts === null) {
    throw new Error(`'${String(value)}' is not a date and time with an offset: write it as 2026-03-04T05:06:07.1230000+02:00.`);
  }

  const [, date, hour, minute, second, fraction, offset] = parts;
  let written = offset!;
  if (written === "Z") {
    written = "+00:00";
  }

  return `${date}T${hour}:${minute}:${second ?? "00"}.${(fraction ?? "").padEnd(7, "0")}${written}`;
}

const spanPattern = /^(-)?(?:(\d+)\.)?(\d{1,2}):(\d{2})(?::(\d{2})(?:\.(\d{1,7}))?)?$/;

// TimeSpan's constant form: [-][d.]hh:mm:ss[.fffffff], the fraction only where there is one.
function timeSpan(value: Exclude<Literal, null>): string {
  const parts = spanPattern.exec(text(value, "TimeSpan"));
  if (parts === null) {
    throw new Error(`'${String(value)}' is not an elapsed time: write it as 1.02:03:04.5000000, the days optional.`);
  }

  const [, negative, days, hours, minutes, seconds, fraction] = parts;
  let written = `${negative ?? ""}`;
  if (days !== undefined &&
      Number(days) !== 0) {
    written += `${Number(days)}.`;
  }

  written += `${hours!.padStart(2, "0")}:${minutes}:${seconds ?? "00"}`;
  const ticks = (fraction ?? "").padEnd(7, "0");
  if (ticks !== "0000000") {
    written += `.${ticks}`;
  }

  return written;
}

const guidPattern = /^\{?([0-9a-f]{8})-?([0-9a-f]{4})-?([0-9a-f]{4})-?([0-9a-f]{4})-?([0-9a-f]{12})\}?$/i;

function guid(value: Exclude<Literal, null>): string {
  const parts = guidPattern.exec(text(value, "Guid"));
  if (parts === null) {
    throw new Error(`'${String(value)}' is not a Guid.`);
  }

  return parts.slice(1).join("-").toLowerCase();
}

/**
 * An enum value by name, a [Flags] one's names joined as .NET joins them; `System.DayOfWeek` alone
 * by its number, since the server compares it with a day it computes as one.
 */
function enumeration(value: Exclude<Literal, null>, name: string, info: EnumInfo): [string, ClrTypeTag] {
  if (info.system === "System.DayOfWeek") {
    const index = info.values.indexOf(text(value, name));
    if (index < 0) {
      throw new Error(`'${String(value)}' is not a ${name}.`);
    }

    return [info.constants[index]!, "Int32"];
  }

  if (!info.flags) {
    const named = text(value, name);
    if (!info.values.includes(named)) {
      throw new Error(`'${named}' is not a ${name}.`);
    }

    return [named, "Enum"];
  }

  return [flags(value, name, info), "Enum"];
}

// A [Flags] value, from a name, a list of names, or names already joined, written as Enum.ToString
// writes it: the names of the bits it holds, lowest first.
function flags(value: Exclude<Literal, null>, name: string, info: EnumInfo): string {
  let names: readonly string[];
  if (Array.isArray(value)) {
    names = value as readonly string[];
  } else {
    names = text(value, name).split(",").map(_ => _.trim());
  }

  const constants = new Map(info.values.map((_, index) => [_, BigInt(info.constants[index]!)]));
  let combined = 0n;
  for (const flag of names) {
    const constant = constants.get(flag);
    if (constant === undefined) {
      throw new Error(`'${flag}' is not a ${name}.`);
    }

    combined |= constant;
  }

  return formatFlags(combined, info);
}

function formatFlags(value: bigint, info: EnumInfo): string {
  const named = info.values.map((_, index) => ({ name: _, value: BigInt(info.constants[index]!) }));
  const exact = named.find(_ => _.value === value);
  if (exact !== undefined) {
    return exact.name;
  }

  // As .NET does: the largest names first, each taken where all its bits remain, then written in
  // ascending order.
  let remaining = value;
  const taken: { name: string; value: bigint }[] = [];
  for (const flag of [...named].sort((left, right) => compare(right.value, left.value))) {
    if (flag.value !== 0n &&
        (remaining & flag.value) === flag.value) {
      taken.push(flag);
      remaining &= ~flag.value;
    }
  }

  if (remaining !== 0n) {
    return value.toString();
  }

  return taken.sort((left, right) => compare(left.value, right.value)).map(_ => _.name).join(", ");
}

function compare(left: bigint, right: bigint): number {
  if (left < right) {
    return -1;
  }

  if (left > right) {
    return 1;
  }

  return 0;
}

const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/** Bytes as base64, padded, as Convert.ToBase64String writes them. */
export function base64(bytes: Uint8Array): string {
  let out = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index]!;
    const second = bytes[index + 1];
    const third = bytes[index + 2];
    out += alphabet.charAt(first >> 2);
    out += alphabet.charAt(((first & 0x03) << 4) | ((second ?? 0) >> 4));
    if (second === undefined) {
      out += "==";
      break;
    }

    out += alphabet.charAt(((second & 0x0f) << 2) | ((third ?? 0) >> 6));
    if (third === undefined) {
      out += "=";
      break;
    }

    out += alphabet.charAt(third & 0x3f);
  }

  return out;
}
