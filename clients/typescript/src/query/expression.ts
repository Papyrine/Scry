// What a query is written with at run time. A model's members are read off a proxy that records the
// path rather than a value, and everything read or computed is an Expression: a wire node and the
// type of the value it stands for, which is what a constant beside it is typed by.

import type { AggregateNode, Node, SubqueryNode } from "../wire.ts";
import type { EnumInfo, MemberInfo, ModelInfo, ScrySchema, ValueInfo } from "../runtime/schema.ts";
import { constant, type Literal, type Position } from "../request/values.ts";

/** What every expression of one query reads its model's description from. */
export class Scope {
  readonly schema: ScrySchema;

  constructor(schema: ScrySchema) {
    this.schema = schema;
  }

  enumInfo = (name: string): EnumInfo | undefined => this.schema.enums[name] ?? builtIn[name];

  model(name: string): ModelInfo {
    const model = this.schema.models[name];
    if (model === undefined) {
      throw new Error(`The model '${name}' is not described.`);
    }

    return model;
  }

  /** A member, through the model's bases, by the name it travels under: its name in a query, and it. */
  memberNamed(model: string, wire: string): [string, MemberInfo] {
    let current: string | undefined = model;
    while (current !== undefined) {
      const described = this.model(current);
      for (const [name, member] of Object.entries(described.members)) {
        if (member.name === wire) {
          return [name, member];
        }
      }

      current = described.base;
    }

    throw new Error(`${model} has no member '${wire}'.`);
  }

  /** Whether a row of the model holds anything marked sensitive, itself or a member. */
  marks(model: string): boolean {
    let current: string | undefined = model;
    while (current !== undefined) {
      const described = this.model(current);
      if (described.sensitive === true ||
          Object.values(described.members).some(_ => _.sensitive === true)) {
        return true;
      }

      current = described.base;
    }

    return false;
  }

  /** A member, through the model's bases, by the name a query was written with. */
  member(model: string, name: string): MemberInfo | undefined {
    let current: string | undefined = model;
    while (current !== undefined) {
      const described = this.model(current);
      const member = Object.hasOwn(described.members, name) ? described.members[name] : undefined;
      if (member !== undefined) {
        return member;
      }

      current = described.base;
    }

    return undefined;
  }
}

// DayOfWeek is what a date's day of the week is, whether or not the model names the enum anywhere.
const builtIn: Readonly<Record<string, EnumInfo>> = {
  DayOfWeek: {
    values: ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"],
    constants: ["0", "1", "2", "3", "4", "5", "6"],
    flags: false,
    underlying: "int",
    system: "System.DayOfWeek"
  }
};

/** The type of the value an expression stands for, where it is known. */
export interface Typed {
  readonly value: ValueInfo | undefined;
  readonly nullable: boolean;
}

export type Side = "Outer" | "Inner";

const misused =
  "An expression is not a value, so JavaScript's own operators ask nothing of the server: compare " +
  "with eq(), gt() and the rest, combine with and(), or() and not(), and compute with add(), concat() " +
  "and the functions beside them.";

/** A value inside a query: the node that travels, and what it is. */
export class Expression implements Typed {
  readonly node: Node;
  readonly value: ValueInfo | undefined;
  readonly nullable: boolean;
  readonly scope: Scope | undefined;
  /** It reads a member the model marks sensitive. */
  readonly sensitive: boolean;
  /** For a member read in a join's result: which row it is read from. */
  readonly side: Side | undefined;

  constructor(node: Node, typed: Typed, scope: Scope | undefined, sensitive: boolean, side?: Side) {
    this.node = node;
    this.value = typed.value;
    this.nullable = typed.nullable;
    this.scope = scope;
    this.sensitive = sensitive;
    this.side = side;
  }

  [Symbol.toPrimitive](): never {
    throw new Error(misused);
  }

  valueOf(): never {
    throw new Error(misused);
  }

  toString(): never {
    throw new Error(misused);
  }

  toJSON(): never {
    throw new Error(misused);
  }
}

export function isExpression(value: unknown): value is Expression {
  return value instanceof Expression;
}

/** A literal or an expression, as an expression: a literal typed by what stands beside it. */
export function operand(value: unknown, beside: Typed | undefined, position: Position, scope: Scope | undefined): Expression {
  if (value instanceof Expression) {
    return value;
  }

  if (isRow(value) ||
      value instanceof Collection) {
    throw new Error("A navigation or a collection is not a value to compare: read a member of it.");
  }

  const enums = scope?.enumInfo ?? (() => undefined);
  const node = constant(value as Literal, beside?.value, position, enums);
  return new Expression(node, { value: beside?.value, nullable: value === null }, scope, false);
}

/** The scope a set of operands share, from whichever carries one. */
export function scopeOf(...values: unknown[]): Scope | undefined {
  for (const value of values) {
    if (value instanceof Expression &&
        value.scope !== undefined) {
      return value.scope;
    }
  }

  return undefined;
}

const rowState = Symbol("row");

interface RowState {
  readonly scope: Scope;
  readonly model: string;
  readonly path: readonly string[];
  /** Read through a navigation that may be absent, so every value read off it may be null. */
  readonly optional: boolean;
  readonly sensitive: boolean;
  readonly side: Side | undefined;
}

export function isRow(value: unknown): boolean {
  return typeof value === "object" &&
         value !== null &&
         (value as Record<symbol, unknown>)[rowState] !== undefined;
}

export function rowStateOf(value: unknown): RowState | undefined {
  if (!isRow(value)) {
    return undefined;
  }

  return (value as Record<symbol, RowState>)[rowState];
}

/** A row of a model, whose members are read as paths. */
export function row(scope: Scope, model: string, path: readonly string[], optional: boolean, side?: Side, sensitive = false): object {
  const state: RowState = { scope, model, path, optional, sensitive: sensitive || scope.model(model).sensitive === true, side };
  return new Proxy(Object.create(null) as object, {
    get(_target, property) {
      if (property === rowState) {
        return state;
      }

      if (typeof property === "symbol") {
        if (property === Symbol.toPrimitive) {
          return () => {
            throw new Error(misused);
          };
        }

        return undefined;
      }

      // Asked by anything deciding whether a value is a promise.
      if (property === "then") {
        return undefined;
      }

      return read(state, property);
    },
    set() {
      throw new Error("A query's row is read, never written.");
    }
  });
}

function read(state: RowState, name: string): unknown {
  const member = state.scope.member(state.model, name);
  if (member === undefined) {
    throw new Error(`'${name}' is not a member of ${state.model.replace(/QueryModel$/, "")}.`);
  }

  const path = [...state.path, member.name];
  const sensitive = state.sensitive || member.sensitive === true;
  const shape = member.shape;
  switch (shape.kind) {
    case "value":
      return new Expression(
        { $type: "member", path },
        { value: shape.value, nullable: shape.nullable || state.optional },
        state.scope,
        sensitive,
        state.side);
    case "navigation":
      return row(state.scope, shape.model, path, true, state.side, sensitive);
    case "rows":
      return new Collection(state.scope, path, { model: shape.model }, sensitive);
    case "values":
      return new Collection(state.scope, path, { value: shape.value, nullable: shape.nullable }, sensitive);
    case "attachment":
      throw new Error(`'${name}' is an attachment, which no query reads: its bytes are fetched by the row's key.`);
  }
}

type Element = { readonly model: string } | { readonly value: ValueInfo; readonly nullable: boolean };

/** What a collection's element is given to a function as. */
function elementOf(collection: Collection): unknown {
  const element = collection.element;
  if ("model" in element) {
    return row(collection.scope, element.model, [], false, undefined, collection.sensitive);
  }

  return new Expression({ $type: "element" }, element, collection.scope, collection.sensitive);
}

type Function_ = SubqueryNode["function"];

/**
 * A collection member: asked about through a subquery, or returned in a projection. `where` narrows
 * the elements either is about, once.
 */
export class Collection {
  readonly scope: Scope;
  readonly path: readonly string[];
  readonly element: Element;
  readonly sensitive: boolean;
  readonly filter: Expression | undefined;

  constructor(scope: Scope, path: readonly string[], element: Element, sensitive: boolean, filter?: Expression) {
    this.scope = scope;
    this.path = path;
    this.element = element;
    this.sensitive = sensitive;
    this.filter = filter;
  }

  where(predicate: (element: never) => unknown): Collection {
    if (this.filter !== undefined) {
      throw new Error(`'${this.path.join(".")}' is already filtered: combine the conditions with and().`);
    }

    return new Collection(this.scope, this.path, this.element, this.sensitive, this.#apply(predicate));
  }

  any(predicate?: (element: never) => unknown): Expression {
    return this.#question("Any", predicate);
  }

  all(predicate: (element: never) => unknown): Expression {
    return this.#question("All", predicate);
  }

  count(predicate?: (element: never) => unknown): Expression {
    return this.#question("Count", predicate);
  }

  /** Whether a collection of values holds a value: whether any element equals it. */
  contains(value: unknown): Expression {
    if ("model" in this.element) {
      throw new Error(`'${this.path.join(".")}' is a collection of rows: ask any() with a condition on them.`);
    }

    const element = elementOf(this) as Expression;
    const compared = operand(value, element, "argument", this.scope);
    const predicate = new Expression(
      { $type: "binary", op: "Equal", left: element.node, right: compared.node },
      { value: { scalar: "bool" }, nullable: false },
      this.scope,
      this.sensitive);
    return this.#subquery("Any", predicate, undefined, { value: { scalar: "bool" }, nullable: false });
  }

  sum(selector?: (element: never) => unknown): Expression {
    return this.#fold("Sum", selector);
  }

  average(selector?: (element: never) => unknown): Expression {
    return this.#fold("Average", selector);
  }

  min(selector?: (element: never) => unknown): Expression {
    return this.#fold("Min", selector);
  }

  max(selector?: (element: never) => unknown): Expression {
    return this.#fold("Max", selector);
  }

  /** The collection returned in a row: what of each element, a value or a flat object of values. */
  select(selector: (element: never) => unknown): CollectionProjection {
    return new CollectionProjection(this, (selector as (element: unknown) => unknown)(elementOf(this)));
  }

  #apply(function_: (element: never) => unknown): Expression {
    const result = (function_ as (element: unknown) => unknown)(elementOf(this));
    if (!(result instanceof Expression)) {
      throw new Error(`A condition on '${this.path.join(".")}' has to be an expression, and this one is ${typeof result}.`);
    }

    return result;
  }

  #question(function_: Function_, predicate: ((element: never) => unknown) | undefined): Expression {
    let condition = this.filter;
    if (predicate !== undefined) {
      if (condition !== undefined) {
        throw new Error(`'${function_}' over '${this.path.join(".")}' cannot combine a where() with its own condition.`);
      }

      condition = this.#apply(predicate);
    }

    let typed: Typed = { value: { scalar: "bool" }, nullable: false };
    if (function_ === "Count") {
      typed = { value: { scalar: "int" }, nullable: false };
    }

    return this.#subquery(function_, condition, undefined, typed);
  }

  #fold(function_: Function_, selector: ((element: never) => unknown) | undefined): Expression {
    let selected: Expression;
    if (selector === undefined) {
      if ("model" in this.element) {
        throw new Error(`'${function_}' over the rows of '${this.path.join(".")}' needs a selector saying what to fold.`);
      }

      selected = elementOf(this) as Expression;
    } else {
      selected = this.#apply(selector);
    }

    let typed: Typed = { value: selected.value, nullable: true };
    if (function_ === "Average" &&
        selected.value !== undefined &&
        "scalar" in selected.value &&
        selected.value.scalar !== "decimal" &&
        selected.value.scalar !== "float") {
      typed = { value: { scalar: "double" }, nullable: true };
    }

    return this.#subquery(function_, this.filter, selected, typed);
  }

  #subquery(function_: Function_, predicate: Expression | undefined, selector: Expression | undefined, typed: Typed): Expression {
    const node: SubqueryNode = {
      $type: "subquery",
      path: [...this.path],
      function: function_,
      ...(predicate === undefined ? {} : { predicate: predicate.node }),
      ...(selector === undefined ? {} : { selector: selector.node })
    };
    return new Expression(node, typed, this.scope, this.sensitive || (predicate?.sensitive ?? false) || (selector?.sensitive ?? false));
  }
}

/** A collection returned in a row, as what `select` made of each element. */
export class CollectionProjection {
  readonly collection: Collection;
  readonly selected: unknown;

  constructor(collection: Collection, selected: unknown) {
    this.collection = collection;
    this.selected = selected;
  }
}

/** The key a grouping was made by, and the nodes it reads as. */
export interface GroupKey {
  readonly keys: readonly Node[];
  /** What `key` reads as in the grouped query: the key's own member, else its position. */
  readonly key: unknown;
  readonly sensitive: boolean;
}

export function groupKey(scope: Scope, selected: unknown): GroupKey {
  if (selected instanceof Expression) {
    return { keys: [selected.node], key: keyRead(selected, 0, scope), sensitive: selected.sensitive };
  }

  if (typeof selected !== "object" ||
      selected === null ||
      isRow(selected)) {
    throw new Error("A grouping key is an expression, or an object of expressions grouping by all of them.");
  }

  const keys: Node[] = [];
  const key: Record<string, unknown> = {};
  let sensitive = false;
  for (const [name, part] of Object.entries(selected)) {
    if (!(part instanceof Expression)) {
      throw new Error(`The grouping key's '${name}' is not an expression.`);
    }

    key[name] = keyRead(part, keys.length, scope);
    keys.push(part.node);
    sensitive ||= part.sensitive;
  }

  if (keys.length === 0) {
    throw new Error("A grouping key needs at least one member.");
  }

  return { keys, key, sensitive };
}

// A key that is a member reads as that member; any other as its position among the keys.
function keyRead(part: Expression, index: number, scope: Scope): Expression {
  if (part.node.$type === "member") {
    return part;
  }

  return new Expression({ $type: "groupKey", index }, part, scope, part.sensitive);
}

/** The rows of one group, folded: what a grouped query's `having` and projection read. */
export class Grouping {
  readonly key: unknown;
  readonly #scope: Scope;
  readonly #model: string;
  readonly #sensitive: boolean;

  constructor(scope: Scope, model: string, key: GroupKey) {
    this.key = key.key;
    this.#scope = scope;
    this.#model = model;
    this.#sensitive = key.sensitive;
  }

  count(predicate?: (row: never) => unknown): Expression {
    let condition: Expression | undefined;
    if (predicate !== undefined) {
      condition = this.#apply(predicate);
    }

    return aggregate(this.#scope, { function: "Count" }, undefined, condition, false, undefined, { value: { scalar: "int" }, nullable: false }, this.#sensitive);
  }

  sum(selector: (row: never) => unknown): Expression {
    return this.values(selector).sum();
  }

  average(selector: (row: never) => unknown): Expression {
    return this.values(selector).average();
  }

  min(selector: (row: never) => unknown): Expression {
    return this.values(selector).min();
  }

  max(selector: (row: never) => unknown): Expression {
    return this.values(selector).max();
  }

  /** One value of every row in the group, to fold: distinct, then counted, summed, or joined. */
  values(selector: (row: never) => unknown): GroupValues {
    return new GroupValues(this.#scope, this.#apply(selector), false, this.#sensitive);
  }

  #apply(function_: (row: never) => unknown): Expression {
    const result = (function_ as (row: unknown) => unknown)(row(this.#scope, this.#model, [], false));
    if (!(result instanceof Expression)) {
      throw new Error(`An aggregate's function has to return an expression, and this one returned ${typeof result}.`);
    }

    return result;
  }
}

export class GroupValues {
  readonly #scope: Scope;
  readonly #selector: Expression;
  readonly #distinct: boolean;
  readonly #sensitive: boolean;

  constructor(scope: Scope, selector: Expression, distinct: boolean, sensitive: boolean) {
    this.#scope = scope;
    this.#selector = selector;
    this.#distinct = distinct;
    this.#sensitive = sensitive;
  }

  distinct(): GroupValues {
    return new GroupValues(this.#scope, this.#selector, true, this.#sensitive);
  }

  count(): Expression {
    return this.#fold("Count", { value: { scalar: "int" }, nullable: false });
  }

  sum(): Expression {
    return this.#fold("Sum", { value: this.#selector.value, nullable: true });
  }

  average(): Expression {
    let value = this.#selector.value;
    if (value !== undefined &&
        "scalar" in value &&
        value.scalar !== "decimal" &&
        value.scalar !== "float") {
      value = { scalar: "double" };
    }

    return this.#fold("Average", { value, nullable: true });
  }

  min(): Expression {
    return this.#fold("Min", { value: this.#selector.value, nullable: true });
  }

  max(): Expression {
    return this.#fold("Max", { value: this.#selector.value, nullable: true });
  }

  /** The values as one string, `separator` between them (string.Join). */
  join(separator: string): Expression {
    return aggregate(this.#scope, { function: "Join" }, this.#selector, undefined, this.#distinct, separator, { value: { scalar: "string" }, nullable: true }, this.#sensitive);
  }

  #fold(function_: AggregateNode["function"], typed: Typed): Expression {
    return aggregate(this.#scope, { function: function_ }, this.#selector, undefined, this.#distinct, undefined, typed, this.#sensitive);
  }
}

function aggregate(
  scope: Scope,
  base: { function: AggregateNode["function"] },
  selector: Expression | undefined,
  predicate: Expression | undefined,
  distinct: boolean,
  separator: string | undefined,
  typed: Typed,
  sensitive: boolean): Expression {
  const node: AggregateNode = {
    $type: "aggregate",
    function: base.function,
    ...(selector === undefined ? {} : { selector: selector.node }),
    ...(separator === undefined ? {} : { separator }),
    ...(predicate === undefined ? {} : { predicate: predicate.node }),
    ...(distinct ? { distinct: true } : {})
  };
  return new Expression(node, typed, scope, sensitive || (selector?.sensitive ?? false) || (predicate?.sensitive ?? false));
}
