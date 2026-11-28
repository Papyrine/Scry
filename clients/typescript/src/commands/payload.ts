// A command's payload as the server binds it: its members camel-cased, a null one left out as the C#
// client's serializer leaves it out, and each value in the JSON type the server reads it from. The
// server reads numbers strictly, so every number is a JSON number, a long and a decimal included;
// an enum travels by its declared name, System.DayOfWeek too; bytes as base64. Members are written in
// the order the description lists them, which is the order every client generated from one has.

import type { CommandInfo, MemberInfo, ValueInfo } from "../runtime/schema.ts";
import { quote } from "../request/json.ts";
import { constant, type EnumLookup, type Literal } from "../request/values.ts";

const numeric: ReadonlySet<string> = new Set(["sbyte", "byte", "short", "ushort", "int", "uint", "long", "ulong", "float", "double", "decimal"]);

// What JSON's grammar calls a number, which is all the server's strict reader takes.
const jsonNumber = /^-?(0|[1-9]\d*)(\.\d+)?([eE][+-]?\d+)?$/;

export function writePayload(command: CommandInfo, payload: unknown, enums: EnumLookup): string {
  if (typeof payload !== "object" ||
      payload === null ||
      Array.isArray(payload)) {
    throw new Error(`The payload of '${command.name}' is an object of its members.`);
  }

  const given = payload as Record<string, unknown>;
  for (const key of Object.keys(given)) {
    if (!Object.hasOwn(command.payload, key)) {
      throw new Error(`The payload of '${command.name}' carries '${key}', which the command does not have.`);
    }
  }

  const written: string[] = [];
  for (const [key, member] of Object.entries(command.payload)) {
    const value = given[key];
    if (value === undefined ||
        value === null) {
      if (value === null &&
          !mayBeNull(member)) {
        throw new Error(`'${key}' in the payload of '${command.name}' cannot be null.`);
      }

      continue;
    }

    written.push(`${quote(key)}:${memberJson(value, member, `'${key}' in the payload of '${command.name}'`, enums)}`);
  }

  return `{${written.join(",")}}`;
}

// A null where the C# client would have one: a nullable value, or a string, bytes or a list, which
// introspection cannot say are nullable and which a C# client leaves null when never set.
function mayBeNull(member: MemberInfo): boolean {
  const shape = member.shape;
  if (shape.kind === "values") {
    return true;
  }

  if (shape.kind !== "value") {
    return false;
  }

  if (shape.nullable) {
    return true;
  }

  return "scalar" in shape.value &&
         (shape.value.scalar === "string" || shape.value.scalar === "bytes");
}

function memberJson(value: unknown, member: MemberInfo, where: string, enums: EnumLookup): string {
  const shape = member.shape;
  if (shape.kind === "value") {
    return valueJson(value, shape.value, where, enums);
  }

  if (shape.kind === "values") {
    if (!Array.isArray(value)) {
      throw new Error(`${where} is a list.`);
    }

    return `[${value.map((item: unknown, index) => {
      if (item === null ||
          item === undefined) {
        throw new Error(`An element of ${where} cannot be null.`);
      }

      return valueJson(item, shape.value, `${where}[${index}]`, enums);
    }).join(",")}]`;
  }

  throw new Error(`${where} is a '${shape.kind}', which a payload cannot carry.`);
}

function valueJson(value: unknown, type: ValueInfo, where: string, enums: EnumLookup): string {
  let text: string;
  try {
    text = spelled(value, type, enums);
  } catch (error) {
    throw new Error(`${where}: ${(error as Error).message}`, { cause: error });
  }

  if ("enum" in type) {
    return quote(text);
  }

  if (type.scalar === "bool") {
    return text;
  }

  if (numeric.has(type.scalar)) {
    if (!jsonNumber.test(text)) {
      throw new Error(`${where}: ${text} is not a number JSON can carry.`);
    }

    return text;
  }

  return quote(text);
}

// The invariant text a constant of the type is sent as, but by name for every enum: a payload is bound,
// not compared, and the binder reads an enum only by its declared name.
function spelled(value: unknown, type: ValueInfo, enums: EnumLookup): string {
  const byName: EnumLookup = name => {
    const info = enums(name);
    if (info?.system === undefined) {
      return info;
    }

    return { values: info.values, constants: info.constants, flags: info.flags, underlying: info.underlying };
  };
  const node = constant(value as Literal, type, "argument", byName);
  return node.value ?? "";
}
