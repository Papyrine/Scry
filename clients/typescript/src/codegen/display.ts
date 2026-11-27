// Reads a member's type display: the exact C# the generator emits for it, which is how introspection
// says what a member is. The grammar is closed, mirroring Schema.ScalarShape and Schema.DescribeShape on
// the server: a keyword or a qualified BCL type, an enum, a query model, a list of one of those, or the
// attachment handle, each scalar, enum or model optionally followed by '?'. Anything else is refused
// rather than read as something near it, so a server that grows a new spelling fails generation loudly.

import type { Scalar, ValueInfo } from "../runtime/schema.ts";

export type Display =
  | { readonly kind: "value"; readonly value: ValueInfo; readonly nullable: boolean }
  | { readonly kind: "model"; readonly model: string }
  | { readonly kind: "list"; readonly element: Display }
  | { readonly kind: "attachment" };

// Mirrors Schema.ScalarDisplay.
const scalars: ReadonlyMap<string, Scalar> = new Map<string, Scalar>([
  ["bool", "bool"],
  ["char", "char"],
  ["sbyte", "sbyte"],
  ["byte", "byte"],
  ["short", "short"],
  ["ushort", "ushort"],
  ["int", "int"],
  ["uint", "uint"],
  ["long", "long"],
  ["ulong", "ulong"],
  ["float", "float"],
  ["double", "double"],
  ["decimal", "decimal"],
  ["string", "string"],
  ["byte[]", "bytes"],
  ["global::System.DateTime", "DateTime"],
  ["global::System.DateOnly", "DateOnly"],
  ["global::System.TimeOnly", "TimeOnly"],
  ["global::System.DateTimeOffset", "DateTimeOffset"],
  ["global::System.TimeSpan", "TimeSpan"],
  ["global::System.Guid", "Guid"]
]);

const list = "global::System.Collections.Generic.IReadOnlyList<";
const attachment = "global::Scry.ScryAttachment";

/** What a display may name besides a scalar. */
export interface Known {
  /** An enum's TypeScript name, by the display that spells it. */
  readonly enums: ReadonlyMap<string, string>;
  /** Every query model the description declares. */
  readonly models: ReadonlySet<string>;
}

export function parseDisplay(display: string, known: Known): Display {
  if (display === attachment) {
    return { kind: "attachment" };
  }

  if (display.startsWith(list) &&
      display.endsWith(">")) {
    const element = parseDisplay(display.slice(list.length, -1), known);
    if (element.kind === "list" ||
        element.kind === "attachment") {
      throw new Error(`The type display '${display}' is a list of something a list cannot hold.`);
    }

    return { kind: "list", element };
  }

  let name = display;
  let nullable = false;
  if (name.endsWith("?")) {
    name = name.slice(0, -1);
    nullable = true;
  }

  const scalar = scalars.get(name);
  if (scalar !== undefined) {
    return { kind: "value", value: { scalar }, nullable };
  }

  const enumeration = known.enums.get(name);
  if (enumeration !== undefined) {
    return { kind: "value", value: { enum: enumeration }, nullable };
  }

  // A navigation is spelled with '?' whether or not the model declares it nullable, since the row it
  // reaches may be absent; a model as a list's element is spelled without.
  if (known.models.has(name)) {
    return { kind: "model", model: name };
  }

  throw new Error(`The type display '${display}' is not one this generator knows. It is a scalar, enum or model the server describes and this client cannot yet spell: generate with a client that knows it.`);
}
