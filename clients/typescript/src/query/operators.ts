// The operators a query is written with, in place of JavaScript's own: an expression is not a value,
// so `a > b` or `a && b` asks nothing of the server. Each builds the node the C# translator builds for
// the same C#, a constant beside an operand typed as C# would have typed it.

import type { BinaryOp, Node, StringMatch } from "../wire.ts";
import type { Scalar, ValueInfo } from "../runtime/schema.ts";
import type * as scry from "../runtime/types.ts";
import { Expression, operand, scopeOf, type Scope, type Typed } from "./expression.ts";
import type { Rows } from "./query.ts";
import { inSource } from "./query.ts";

/** A value an operand of type `T` may be written as, beside the expression it is compared with. */
export type Plain<T> =
  T extends scry.Decimal ? scry.Decimal | string | number | bigint :
  T extends scry.Guid ? scry.Guid | string :
  T extends scry.DateTime ? scry.DateTime | string | Date :
  T extends scry.DateOnly ? scry.DateOnly | string | Date :
  T extends scry.TimeOnly ? scry.TimeOnly | string :
  T extends scry.TimeSpan ? scry.TimeSpan | string :
  T extends scry.DateTimeOffset ? scry.DateTimeOffset | string | Date :
  T extends scry.Flags<infer V> ? scry.Flags<V> | V | readonly V[] :
  T extends bigint ? bigint | number :
  T extends number ? number | bigint :
  T extends string ? T :
  T extends boolean ? boolean :
  T extends Uint8Array ? Uint8Array :
  never;

/** An operand of type `T`: an expression of it, or a value of it. */
export type Operand<T> = scry.Expr<T> | scry.Expr<NonNullable<T>> | scry.Expr<NonNullable<T> | null> | Plain<NonNullable<T>> | null;

type Condition = scry.Expr<boolean> | scry.Expr<boolean | null>;

const bool: Typed = { value: { scalar: "bool" }, nullable: false };

function binary(op: BinaryOp, left: Expression, right: Expression, typed: Typed): Expression {
  return new Expression(
    { $type: "binary", op, left: left.node, right: right.node },
    typed,
    scopeOf(left, right),
    left.sensitive || right.sensitive);
}

// The two sides of an operator, a constant typed by the expression beside it.
function sides(left: unknown, right: unknown): [Expression, Expression] {
  const scope = scopeOf(left, right);
  if (left instanceof Expression) {
    return [left, operand(right, left, "operand", scope)];
  }

  if (right instanceof Expression) {
    return [operand(left, right, "operand", scope), right];
  }

  return [operand(left, undefined, "operand", scope), operand(right, undefined, "operand", scope)];
}

function comparison(op: BinaryOp, left: unknown, right: unknown): Expression {
  const [l, r] = sides(left, right);
  return binary(op, l, r, bool);
}

export function eq<T>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<boolean>;
export function eq<T>(left: Operand<NoInfer<T>>, right: scry.Expr<T>): scry.Expr<boolean>;
export function eq(left: unknown, right: unknown): unknown {
  return comparison("Equal", left, right);
}

export function ne<T>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<boolean>;
export function ne<T>(left: Operand<NoInfer<T>>, right: scry.Expr<T>): scry.Expr<boolean>;
export function ne(left: unknown, right: unknown): unknown {
  return comparison("NotEqual", left, right);
}

export function lt<T>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<boolean>;
export function lt<T>(left: Operand<NoInfer<T>>, right: scry.Expr<T>): scry.Expr<boolean>;
export function lt(left: unknown, right: unknown): unknown {
  return comparison("LessThan", left, right);
}

export function le<T>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<boolean>;
export function le<T>(left: Operand<NoInfer<T>>, right: scry.Expr<T>): scry.Expr<boolean>;
export function le(left: unknown, right: unknown): unknown {
  return comparison("LessThanOrEqual", left, right);
}

export function gt<T>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<boolean>;
export function gt<T>(left: Operand<NoInfer<T>>, right: scry.Expr<T>): scry.Expr<boolean>;
export function gt(left: unknown, right: unknown): unknown {
  return comparison("GreaterThan", left, right);
}

export function ge<T>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<boolean>;
export function ge<T>(left: Operand<NoInfer<T>>, right: scry.Expr<T>): scry.Expr<boolean>;
export function ge(left: unknown, right: unknown): unknown {
  return comparison("GreaterThanOrEqual", left, right);
}

/** Whether a value is absent, as `x == null`. */
export function isNull(value: scry.Expr<unknown>): scry.Expr<boolean> {
  return comparison("Equal", value, null) as never;
}

/** Whether a value is present, as `x.HasValue` or `x != null`. */
export function hasValue(value: scry.Expr<unknown>): scry.Expr<boolean> {
  return comparison("NotEqual", value, null) as never;
}

function logical(op: BinaryOp, conditions: readonly unknown[]): Expression {
  if (conditions.length < 2) {
    throw new Error(`${op} combines two conditions or more.`);
  }

  let combined = condition(conditions[0]);
  for (const next of conditions.slice(1)) {
    combined = binary(op, combined, condition(next), bool);
  }

  return combined;
}

function condition(value: unknown): Expression {
  if (!(value instanceof Expression)) {
    throw new Error(`A condition has to be an expression, and this one is ${typeof value}. JavaScript's own !, && and || ask nothing of the server: use not(), and() and or().`);
  }

  return value;
}

/** Every condition holds (`&&`), combined left to right as C# does. */
export function and(...conditions: [Condition, Condition, ...Condition[]]): scry.Expr<boolean> {
  return logical("AndAlso", conditions) as never;
}

/** Any condition holds (`||`), combined left to right as C# does. */
export function or(...conditions: [Condition, Condition, ...Condition[]]): scry.Expr<boolean> {
  return logical("OrElse", conditions) as never;
}

export function not(value: Condition): scry.Expr<boolean> {
  const operand = condition(value);
  return new Expression({ $type: "unary", op: "Not", operand: operand.node }, bool, operand.scope, operand.sensitive) as never;
}

type Numeric = number | bigint | scry.Decimal;

const narrow = new Set<Scalar>(["sbyte", "byte", "short", "ushort", "char", "int"]);
const signed = new Set<Scalar>(["sbyte", "short", "int", "long"]);

function scalarOf(value: ValueInfo | undefined): Scalar | undefined {
  if (value === undefined ||
      !("scalar" in value)) {
    return undefined;
  }

  return value.scalar;
}

// The type an arithmetic operator works in, as C#'s binary numeric promotion decides it.
function promoted(left: ValueInfo | undefined, right: ValueInfo | undefined): ValueInfo | undefined {
  const l = scalarOf(left);
  const r = scalarOf(right);
  if (l === undefined ||
      r === undefined) {
    return left ?? right;
  }

  for (const wide of ["decimal", "double", "float", "ulong", "long"] as const) {
    if (l === wide ||
        r === wide) {
      return { scalar: wide };
    }
  }

  if ((l === "uint" && signed.has(r)) ||
      (r === "uint" && signed.has(l))) {
    return { scalar: "long" };
  }

  if (l === "uint" ||
      r === "uint") {
    return { scalar: "uint" };
  }

  if (narrow.has(l) &&
      narrow.has(r)) {
    return { scalar: "int" };
  }

  return left;
}

// What a constant was typed as, from its tag, where that differs from the operand beside it.
function constantType(expression: Expression): ValueInfo | undefined {
  if (expression.node.$type !== "const") {
    return expression.value;
  }

  switch (expression.node.tag) {
    case "Int64":
      return { scalar: "long" };
    case "Double":
      return { scalar: "double" };
    default:
      return expression.value;
  }
}

function arithmetic(op: BinaryOp, left: unknown, right: unknown): Expression {
  const [l, r] = sides(left, right);
  return binary(op, l, r, { value: promoted(constantType(l), constantType(r)), nullable: l.nullable || r.nullable });
}

export function add<T extends Numeric | null>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<T> {
  return arithmetic("Add", left, right) as never;
}

export function sub<T extends Numeric | null>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<T> {
  return arithmetic("Subtract", left, right) as never;
}

export function mul<T extends Numeric | null>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<T> {
  return arithmetic("Multiply", left, right) as never;
}

export function div<T extends Numeric | null>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<T> {
  return arithmetic("Divide", left, right) as never;
}

export function mod<T extends Numeric | null>(left: scry.Expr<T>, right: Operand<NoInfer<T>>): scry.Expr<T> {
  return arithmetic("Modulo", left, right) as never;
}

export function neg<T extends Numeric | null>(value: scry.Expr<T>): scry.Expr<T> {
  const operand = value as unknown as Expression;
  return new Expression({ $type: "unary", op: "Negate", operand: operand.node }, operand, operand.scope, operand.sensitive) as never;
}

/** The value, or `fallback` where it is null (`??`). */
export function coalesce<T>(value: scry.Expr<T | null>, fallback: Operand<NoInfer<T>>): scry.Expr<NonNullable<T>> {
  const [l, r] = sides(value, fallback);
  return binary("Coalesce", l, r, { value: l.value ?? r.value, nullable: r.nullable }) as never;
}

/** `test ? ifTrue : ifFalse`, a constant branch typed by the other. */
export function cond<T>(test: Condition, ifTrue: scry.Expr<T> | Plain<NonNullable<T>> | null, ifFalse: scry.Expr<T> | Plain<NonNullable<T>> | null): scry.Expr<T> {
  const checked = condition(test);
  const [whenTrue, whenFalse] = sides(ifTrue, ifFalse);
  return new Expression(
    { $type: "conditional", test: checked.node, ifTrue: whenTrue.node, ifFalse: whenFalse.node },
    { value: whenTrue.value ?? whenFalse.value, nullable: whenTrue.nullable || whenFalse.nullable },
    scopeOf(checked, whenTrue, whenFalse),
    checked.sensitive || whenTrue.sensitive || whenFalse.sensitive) as never;
}

/**
 * Values joined into one string (`a + b`, or an interpolated string), left to right as C# folds them.
 * Anything that is not text is converted on the server.
 */
export function concat(first: scry.Expr<unknown> | string, ...rest: [scry.Expr<unknown> | string | number | bigint | boolean, ...(scry.Expr<unknown> | string | number | bigint | boolean)[]]): scry.Expr<string> {
  const scope = scopeOf(first, ...rest);
  let joined = operand(first, undefined, "argument", scope);
  for (const next of rest) {
    const part = operand(next, undefined, "argument", scope);
    joined = new Expression(
      { $type: "call", function: "StringConcat", target: joined.node, arguments: [part.node] },
      { value: { scalar: "string" }, nullable: false },
      scope,
      joined.sensitive || part.sensitive);
  }

  return joined as never;
}

function collate(value: unknown, match: StringMatch): Expression {
  if (!(value instanceof Expression)) {
    throw new Error("Only an expression is compared under a case sensitivity.");
  }

  return new Expression({ $type: "collate", target: value.node, match }, value, value.scope, value.sensitive);
}

/**
 * The text compared without regard to case, under the collation the server is configured with: what
 * `StringComparison.OrdinalIgnoreCase` asks for in C#.
 */
export function ignoringCase<T extends string | null>(value: scry.Expr<T>): scry.Expr<T> {
  return collate(value, "CaseInsensitive") as never;
}

/** The text compared with regard to case: what `StringComparison.Ordinal` asks for in C#. */
export function matchingCase<T extends string | null>(value: scry.Expr<T>): scry.Expr<T> {
  return collate(value, "CaseSensitive") as never;
}

/**
 * Whether a value is one of a list (SQL `IN`), or one of the values another source holds where its
 * filter does. Each value of a list keeps the member's own type, as the elements of a C# array would.
 */
export function isIn<T>(value: scry.Expr<T>, values: readonly Plain<NonNullable<T>>[]): scry.Expr<boolean>;
export function isIn<T, Q, R, S>(value: scry.Expr<T>, source: Rows<Q, R, S>, selector: (_: Q) => scry.Expr<NoInfer<T>> | scry.Expr<NonNullable<NoInfer<T>>>): scry.Expr<boolean>;
export function isIn(value: unknown, values: unknown, selector?: (_: never) => unknown): unknown {
  const target = value as Expression;
  if (selector !== undefined) {
    return inSource(target, values, selector);
  }

  const list = values as readonly unknown[];
  if (list.length === 0) {
    throw new Error("A list to look a value up in needs at least one value.");
  }

  const nodes: Node[] = list.map(_ => operand(_, target, "argument", target.scope).node);
  return new Expression({ $type: "call", function: "In", target: target.node, arguments: nodes }, bool, target.scope, target.sensitive);
}

export type { Scope };
