// The description of a model that generated code carries beside its types, for what the types cannot
// say at run time: the name each member travels under, what each value is, and what a query that
// projects nothing projects. Generated from the server's introspection and keyed by the TypeScript
// names, so a member is found by the name a query was written with.

/** A scalar, by the C# keyword introspection spells it with. */
export type Scalar =
  | "bool"
  | "char"
  | "sbyte"
  | "byte"
  | "short"
  | "ushort"
  | "int"
  | "uint"
  | "long"
  | "ulong"
  | "float"
  | "double"
  | "decimal"
  | "string"
  | "bytes"
  | "DateTime"
  | "DateOnly"
  | "TimeOnly"
  | "DateTimeOffset"
  | "TimeSpan"
  | "Guid";

/** What a single value is: a scalar, or an enum named in `ScrySchema.enums`. */
export type ValueInfo =
  | { readonly scalar: Scalar }
  | { readonly enum: string };

/** What a member is. */
export type ShapeInfo =
  | { readonly kind: "value"; readonly value: ValueInfo; readonly nullable: boolean }
  | { readonly kind: "navigation"; readonly model: string }
  | { readonly kind: "rows"; readonly model: string }
  | { readonly kind: "values"; readonly value: ValueInfo; readonly nullable: boolean }
  | { readonly kind: "attachment"; readonly contentType?: string };

export interface MemberInfo {
  /** The name the member travels under: the server's, as a path segment names it. */
  readonly name: string;
  readonly shape: ShapeInfo;
  /** A query comparing it with a constant has to travel as a body, never a URL. */
  readonly sensitive?: true;
  /** The command a capability member answers for. */
  readonly capability?: string;
  /** Present when the member is deprecated: its message, or empty. */
  readonly obsolete?: string;
}

export interface ModelInfo {
  /** The model this one derives from, whose members are this one's too. */
  readonly base?: string;
  /** Every member read off this model is sensitive. */
  readonly sensitive?: true;
  /** The members forming the row's key, by server name, for a model carrying an attachment. */
  readonly keys?: readonly string[];
  readonly obsolete?: string;
  /**
   * What a query writing no projection projects when its rows are this model, by server name: the
   * scalar members, a base's first, in the order the model declares them. Every client sends the same
   * list, so every one sends the same request.
   */
  readonly defaultProjection: readonly string[];
  /** The members this model declares, by TypeScript name; a base's are on the base. */
  readonly members: { readonly [name: string]: MemberInfo };
}

export interface SourceInfo {
  /** The name a query's root names. */
  readonly name: string;
  readonly kind: string;
  /** The model a row of this source is, a key of `ScrySchema.models`. */
  readonly model: string;
  readonly obsolete?: string;
}

export interface EnumInfo {
  /** The names, which is what travels. */
  readonly values: readonly string[];
  /** The underlying number of each name, in the same order. */
  readonly constants: readonly string[];
  readonly flags: boolean;
  readonly underlying: string;
  /**
   * The base class library type, for an enum that is one rather than the model's own: `System.DayOfWeek`
   * travels as its number where every other enum travels by name.
   */
  readonly system?: string;
}

export interface CommandInfo {
  /** The name a command travels under. */
  readonly name: string;
  /** The source a targeted command acts on. */
  readonly target?: string;
  /** The payload members a targeted command's row is found by, by server name. */
  readonly keys?: readonly string[];
  /** The payload, by TypeScript name, which is also how it travels: camel-cased. */
  readonly payload: { readonly [name: string]: MemberInfo };
  /** What the command completes with. */
  readonly result?: { readonly name: string; readonly members: { readonly [name: string]: MemberInfo } };
  readonly obsolete?: string;
}

export interface ScrySchema {
  /** The schema stamp of the description this was generated from. */
  readonly stamp: string;
  readonly maxPageSize: number;
  readonly queryUrlLimit: number;
  /** By TypeScript name. */
  readonly sources: { readonly [name: string]: SourceInfo };
  /** By the server's model name. */
  readonly models: { readonly [name: string]: ModelInfo };
  /** By TypeScript name, which is the enum's own. */
  readonly enums: { readonly [name: string]: EnumInfo };
  /** By TypeScript name. */
  readonly commands: { readonly [name: string]: CommandInfo };
}
