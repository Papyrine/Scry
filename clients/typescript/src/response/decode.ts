// Reads a payload into what the query said it returns. The plan is built with the query, from what
// each member of its projection reads, so a long becomes a bigint and a decimal keeps every digit of
// its text, and each row's keys, which the server camel-cases, become the names the query used. A
// binary value that travelled as a multipart part is read from the part its placeholder names, and
// an attachment, which no response carries, is a handle made from the key read beside it.

import { ScryBinary, type AttachmentKey, type EnumAlias } from "../wire.ts";
import type { EnumInfo, ValueInfo } from "../runtime/schema.ts";
import { camelCase } from "../runtime/naming.ts";
import { ScryStaleClientError, ScryWireError } from "../errors.ts";
import { constant, type Literal } from "../request/values.ts";
import { RawNumber, type Json } from "./json.ts";
import type { Scope } from "../query/expression.ts";

/** What a payload, or a part of one, is. */
export type Plan =
  | { readonly kind: "value"; readonly value: ValueInfo | undefined }
  | { readonly kind: "object"; readonly members: readonly PlanMember[]; readonly attachments?: readonly AttachmentBinding[] }
  | { readonly kind: "array"; readonly element: Plan }
  /** A row of a model, keyed as the server's own projection keys it. */
  | { readonly kind: "model"; readonly model: string; readonly attachments?: readonly AttachmentBinding[] };

/**
 * An attachment a row carries: where its handle goes, what it fetches, and where in the same row each
 * value of its key landed, in the order the key travels in.
 */
export interface AttachmentBinding {
  /** By the names the query wrote. */
  readonly target: readonly string[];
  /** The source the attachment is fetched from. */
  readonly root: string;
  /** The attachment member, by the name it travels under. */
  readonly member: string;
  readonly keys: readonly { readonly path: readonly string[]; readonly value: ValueInfo | undefined }[];
}

/** Makes the handle an attachment is fetched through. */
export type Attach = (root: string, member: string, keys: readonly AttachmentKey[]) => unknown;

export interface PlanMember {
  /** The name the query wrote, which the row is returned under. */
  readonly key: string;
  /** The name it travelled under, which the server camel-cases as the row's key. */
  readonly wire: string;
  readonly plan: Plan;
  /** The server leaves it out where it is null, so a row without it is one without its value. */
  readonly optional?: true;
}

export function valuePlan(value: ValueInfo | undefined): Plan {
  return { kind: "value", value };
}

/** Reads one response's payload, with the enum aliases that response carried. */
export class Decoder {
  readonly #scope: Scope;
  readonly #aliases: readonly EnumAlias[];

  readonly #attach: Attach | undefined;

  /**
   * The binary parts the placeholders in what is read next index: a whole response's, or in a stream
   * the parts that arrived before the row.
   */
  parts: readonly Uint8Array[] | undefined;

  constructor(scope: Scope, aliases: readonly EnumAlias[] | undefined, parts?: readonly Uint8Array[], attach?: Attach) {
    this.#scope = scope;
    this.#aliases = aliases ?? [];
    this.parts = parts;
    this.#attach = attach;
  }

  decode(json: Json, plan: Plan, where = "the payload"): unknown {
    if (json === null) {
      return null;
    }

    switch (plan.kind) {
      case "value":
        return this.#value(json, plan.value, where);
      case "array":
        if (!Array.isArray(json)) {
          throw expected("a list", where);
        }

        return json.map((_, index) => this.decode(_, plan.element, `${where}[${index}]`));
      case "object":
        return this.#bind(this.#object(json, plan.members, where), plan.attachments);
      case "model":
        return this.#bind(this.#model(json, plan.model, where), plan.attachments);
    }
  }

  // Each attachment's handle, made from the key that came back with the row. A row a navigation did not
  // reach has no key and no object to hold a handle.
  #bind(row: Record<string, unknown>, attachments: readonly AttachmentBinding[] | undefined): Record<string, unknown> {
    for (const binding of attachments ?? []) {
      if (this.#attach === undefined) {
        throw new Error("These rows carry attachments, and this reader was given nothing to fetch them through.");
      }

      const keys: AttachmentKey[] = [];
      for (const key of binding.keys) {
        const value = at(row, key.path);
        if (value === null ||
            value === undefined) {
          break;
        }

        const node = constant(value as Literal, key.value, "argument", this.#scope.enumInfo);
        if (node.value === undefined) {
          keys.push({ tag: node.tag });
        } else {
          keys.push({ value: node.value, tag: node.tag });
        }
      }

      const owner = at(row, binding.target.slice(0, -1));
      if (keys.length !== binding.keys.length ||
          typeof owner !== "object" ||
          owner === null) {
        continue;
      }

      (owner as Record<string, unknown>)[binding.target.at(-1)!] = this.#attach(binding.root, binding.member, keys);
    }

    return row;
  }

  #object(json: Json, members: readonly PlanMember[], where: string): Record<string, unknown> {
    const row = record(json, where);
    const out: Record<string, unknown> = {};
    for (const member of members) {
      const key = camelCase(member.wire);
      if (!Object.hasOwn(row, key)) {
        if (member.optional === true) {
          continue;
        }

        throw new ScryWireError(`${where} has no '${key}', which the query projected.`);
      }

      out[member.key] = this.decode(row[key]!, member.plan, `${where}.${key}`);
    }

    return out;
  }

  // A row the server projected itself: each member read as the model describes it, where it does.
  #model(json: Json, model: string, where: string): Record<string, unknown> {
    const row = record(json, where);
    const out: Record<string, unknown> = {};
    for (const [key, value] of Object.entries(row)) {
      const member = this.#scope.member(model, key);
      let plan: Plan = valuePlan(undefined);
      if (member !== undefined) {
        const shape = member.shape;
        switch (shape.kind) {
          case "value":
            plan = valuePlan(shape.value);
            break;
          case "navigation":
            plan = { kind: "model", model: shape.model };
            break;
          case "rows":
            plan = { kind: "array", element: { kind: "model", model: shape.model } };
            break;
          case "values":
            plan = { kind: "array", element: valuePlan(shape.value) };
            break;
          case "attachment":
            break;
        }
      }

      out[key] = this.decode(value, plan, `${where}.${key}`);
    }

    return out;
  }

  #value(json: Json, value: ValueInfo | undefined, where: string): unknown {
    if (value === undefined) {
      return untyped(json);
    }

    if ("enum" in value) {
      return this.#enumeration(json, value.enum, where);
    }

    switch (value.scalar) {
      case "bool":
        if (typeof json !== "boolean") {
          throw expected("true or false", where);
        }

        return json;
      case "sbyte":
      case "byte":
      case "short":
      case "ushort":
      case "int":
      case "uint":
      case "float":
      case "double":
        return Number(numberText(json, where));
      case "long":
      case "ulong":
        return BigInt(numberText(json, where));
      case "decimal":
        return numberText(json, where);
      case "bytes":
        if (typeof json === "object" &&
            json !== null &&
            !Array.isArray(json) &&
            !(json instanceof RawNumber)) {
          return this.#part(json, where);
        }

        return fromBase64(text(json, where), where);
      default:
        return text(json, where);
    }
  }

  // A placeholder names a part by its index. One that names no part, or that arrived where no parts
  // did, fails the read rather than standing for a guess.
  #part(json: { [key: string]: Json }, where: string): Uint8Array {
    const index = json[ScryBinary.PartProperty];
    if (Object.keys(json).length !== 1 ||
        !(index instanceof RawNumber) ||
        !/^\d+$/.test(index.text)) {
      throw expected("base64 text, or a binary part's placeholder", where);
    }

    const part = this.parts?.[Number(index.text)];
    if (part === undefined) {
      throw new ScryWireError(`${where} names binary part ${index.text}, which the response did not carry.`);
    }

    return part;
  }

  // A name this client's model has, or one a rename the response describes leads back to. A value the
  // server computes rather than reads, such as a date's day of the week, arrives as its number.
  #enumeration(json: Json, name: string, where: string): string {
    const info = this.#scope.enumInfo(name);
    if (info === undefined) {
      throw new ScryWireError(`The enum '${name}' is not described.`);
    }

    if (json instanceof RawNumber) {
      return named(json.text, name, info, where);
    }

    const written = text(json, where);
    if (!info.flags) {
      return this.#known(written, name, info);
    }

    return written.split(",").map(_ => this.#known(_.trim(), name, info)).join(", ");
  }

  #known(written: string, name: string, info: EnumInfo): string {
    if (info.values.includes(written)) {
      return written;
    }

    for (const alias of this.#aliases) {
      if (alias.enumName !== name ||
          alias.valueName !== written) {
        continue;
      }

      const previous = alias.previousNames.find(_ => info.values.includes(_));
      if (previous !== undefined) {
        return previous;
      }
    }

    throw new ScryStaleClientError(
      `'${written}' is not a value of enum '${name}' in this client's generated model. The server's model has changed: generate the client again, or reload the deployed app.`);
  }
}

// An enum's number as its name, a [Flags] one's as the names of its bits joined, lowest first.
function named(number: string, name: string, info: EnumInfo, where: string): string {
  const exact = info.constants.indexOf(number);
  if (exact >= 0) {
    return info.values[exact]!;
  }

  if (info.flags &&
      /^\d+$/.test(number)) {
    let remaining = BigInt(number);
    const bits: string[] = [];
    info.constants
      .map((_, index) => ({ name: info.values[index]!, value: BigInt(_) }))
      .sort((left, right) => Number(right.value - left.value))
      .forEach(_ => {
        if (_.value !== 0n &&
            (remaining & _.value) === _.value) {
          bits.unshift(_.name);
          remaining &= ~_.value;
        }
      });
    if (remaining === 0n) {
      return bits.join(", ");
    }
  }

  throw new ScryStaleClientError(`${where} is ${number}, which no value of enum '${name}' in this client's generated model is. Generate the client again.`);
}

function at(row: unknown, path: readonly string[]): unknown {
  let current = row;
  for (const segment of path) {
    if (typeof current !== "object" ||
        current === null) {
      return undefined;
    }

    current = (current as Record<string, unknown>)[segment];
  }

  return current;
}

function record(json: Json, where: string): { [key: string]: Json } {
  if (typeof json !== "object" ||
      json === null ||
      Array.isArray(json) ||
      json instanceof RawNumber) {
    throw expected("an object", where);
  }

  return json;
}

function numberText(json: Json, where: string): string {
  if (!(json instanceof RawNumber)) {
    throw expected("a number", where);
  }

  return json.text;
}

function text(json: Json, where: string): string {
  if (typeof json !== "string") {
    throw expected("text", where);
  }

  return json;
}

function expected(what: string, where: string): ScryWireError {
  return new ScryWireError(`${where} should be ${what}, as the query reads it.`);
}

// A value the query did not type: a number as a JavaScript number, everything else as it is.
function untyped(json: Json): unknown {
  if (json instanceof RawNumber) {
    return Number(json.text);
  }

  if (Array.isArray(json)) {
    return json.map(untyped);
  }

  if (typeof json === "object" &&
      json !== null) {
    return Object.fromEntries(Object.entries(json).map(([key, value]) => [key, untyped(value)]));
  }

  return json;
}

const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
const sextets = new Map([...alphabet].map((_, index) => [_, index]));

/** Base64, as Convert.ToBase64String writes it, back to its bytes. */
export function fromBase64(text: string, where = "the value"): Uint8Array {
  const trimmed = text.replace(/=+$/, "");
  if (trimmed.length % 4 === 1 ||
      text.length % 4 !== 0) {
    throw new ScryWireError(`${where} is not base64.`);
  }

  const bytes = new Uint8Array(Math.floor(trimmed.length * 3 / 4));
  let bits = 0;
  let count = 0;
  let index = 0;
  for (const character of trimmed) {
    const sextet = sextets.get(character);
    if (sextet === undefined) {
      throw new ScryWireError(`${where} is not base64.`);
    }

    bits = (bits << 6) | sextet;
    count += 6;
    if (count >= 8) {
      count -= 8;
      bytes[index++] = (bits >> count) & 0xff;
    }
  }

  return bytes;
}
