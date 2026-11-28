// The closed set of functions a query may call, one helper per KnownFunction (a few reading two, by the
// type of what they are given). Each argument that is a constant is typed as the C# method's parameter
// is, which is what the C# translator sends: Substring's start is an int, AddDays' days a double.

import type { KnownFunction } from "../wire.ts";
import type { Scalar, ValueInfo } from "../runtime/schema.ts";
import type * as scry from "../runtime/types.ts";
import { Expression, operand, type Typed } from "./expression.ts";
import type { Operand, Plain } from "./operators.ts";

type Argument = ValueInfo | "target";

function scalar(type: Scalar): ValueInfo {
  return { scalar: type };
}

function expression(value: unknown, what: string): Expression {
  if (!(value instanceof Expression)) {
    throw new Error(`${what} is called on an expression, a member or something computed from one.`);
  }

  return value;
}

function call(function_: KnownFunction, target: unknown, args: readonly [unknown, Argument][], result: (target: Expression) => Typed): Expression {
  const on = expression(target, function_);
  const nodes = args.map(([value, type]) => {
    let typed: Typed = on;
    if (type !== "target") {
      typed = { value: type, nullable: false };
    }

    return operand(value, typed, "argument", on.scope);
  });
  return new Expression(
    { $type: "call", function: function_, target: on.node, arguments: nodes.map(_ => _.node) },
    result(on),
    on.scope,
    on.sensitive || nodes.some(_ => _.sensitive));
}

const returns = (type: Scalar, nullable = false) => (): Typed => ({ value: scalar(type), nullable });
const same = (target: Expression): Typed => target;

function scalarOf(value: Expression): Scalar | undefined {
  if (value.value === undefined ||
      !("scalar" in value.value)) {
    return undefined;
  }

  return value.value.scalar;
}

type Text = string | null;
type Bytes = Uint8Array | null;
type Moment = scry.DateTime | scry.DateOnly | scry.DateTimeOffset | null;
type Clock = scry.DateTime | scry.DateTimeOffset | scry.TimeOnly | null;
type Number_ = number | bigint | scry.Decimal | null;

// Text.

/** Whether the text holds `value`; over bytes, whether they hold the byte. */
export function contains(text: scry.Expr<Text>, value: Operand<string>): scry.Expr<boolean>;
export function contains(bytes: scry.Expr<Bytes>, value: number): scry.Expr<boolean>;
export function contains(target: unknown, value: unknown): unknown {
  if (scalarOf(expression(target, "contains")) === "bytes") {
    return call("BytesContains", target, [[value, scalar("byte")]], returns("bool"));
  }

  return call("StringContains", target, [[value, scalar("string")]], returns("bool"));
}

export function startsWith(text: scry.Expr<Text>, value: Operand<string>): scry.Expr<boolean> {
  return call("StringStartsWith", text, [[value, scalar("string")]], returns("bool")) as never;
}

export function endsWith(text: scry.Expr<Text>, value: Operand<string>): scry.Expr<boolean> {
  return call("StringEndsWith", text, [[value, scalar("string")]], returns("bool")) as never;
}

export function toLower<T extends Text>(text: scry.Expr<T>): scry.Expr<T> {
  return call("StringToLower", text, [], same) as never;
}

export function toUpper<T extends Text>(text: scry.Expr<T>): scry.Expr<T> {
  return call("StringToUpper", text, [], same) as never;
}

export function isNullOrEmpty(text: scry.Expr<Text>): scry.Expr<boolean> {
  return call("StringIsNullOrEmpty", text, [], returns("bool")) as never;
}

export function isNullOrWhiteSpace(text: scry.Expr<Text>): scry.Expr<boolean> {
  return call("StringIsNullOrWhiteSpace", text, [], returns("bool")) as never;
}

/** The text's length in UTF-16 units, or the number of bytes. */
export function length(value: scry.Expr<Text> | scry.Expr<Bytes>): scry.Expr<number> {
  if (scalarOf(expression(value, "length")) === "bytes") {
    return call("BytesLength", value, [], returns("int")) as never;
  }

  return call("StringLength", value, [], returns("int")) as never;
}

export function trim<T extends Text>(text: scry.Expr<T>): scry.Expr<T> {
  return call("StringTrim", text, [], same) as never;
}

export function trimStart<T extends Text>(text: scry.Expr<T>): scry.Expr<T> {
  return call("StringTrimStart", text, [], same) as never;
}

export function trimEnd<T extends Text>(text: scry.Expr<T>): scry.Expr<T> {
  return call("StringTrimEnd", text, [], same) as never;
}

export function substring<T extends Text>(text: scry.Expr<T>, start: Operand<number>, length?: Operand<number>): scry.Expr<T> {
  const args: [unknown, Argument][] = [[start, scalar("int")]];
  if (length !== undefined) {
    args.push([length, scalar("int")]);
  }

  return call("StringSubstring", text, args, same) as never;
}

export function indexOf(text: scry.Expr<Text>, value: Operand<string>): scry.Expr<number> {
  return call("StringIndexOf", text, [[value, scalar("string")]], returns("int")) as never;
}

export function replace<T extends Text>(text: scry.Expr<T>, oldValue: Operand<string>, newValue: Operand<string>): scry.Expr<T> {
  return call("StringReplace", text, [[oldValue, scalar("string")], [newValue, scalar("string")]], same) as never;
}

/** The first character, or the default where there is none (`FirstOrDefault`). */
export function firstChar(text: scry.Expr<Text>): scry.Expr<string> {
  return call("StringFirst", text, [], returns("char")) as never;
}

/** The last character, or the default where there is none (`LastOrDefault`). */
export function lastChar(text: scry.Expr<Text>): scry.Expr<string> {
  return call("StringLast", text, [], returns("char")) as never;
}

/** The value as text: `ToString()`. */
export function toText(value: scry.Expr<unknown>): scry.Expr<string> {
  return call("StringFrom", value, [], returns("string")) as never;
}

/** -1, 0 or 1 as `value` is before, equal to or after `other`: `CompareTo`, or `string.Compare`. */
export function compare<T>(value: scry.Expr<T>, other: Operand<NoInfer<T>>): scry.Expr<number | null> {
  return call("CompareTo", value, [[other, "target"]], returns("int", true)) as never;
}

// Dates and times.

const part = (function_: KnownFunction) => (value: scry.Expr<Moment> | scry.Expr<Clock>): scry.Expr<number> =>
  call(function_, value, [], returns("int")) as never;

export const year = part("DateYear");
export const month = part("DateMonth");
export const day = part("DateDay");
export const hour = part("DateHour");
export const minute = part("DateMinute");
export const second = part("DateSecond");
export const millisecond = part("DateMillisecond");
export const microsecond = part("DateMicrosecond");
export const nanosecond = part("DateNanosecond");
export const dayOfYear = part("DateDayOfYear");

/** The count of days since 0001-01-01 (`DateOnly.DayNumber`). */
export function dayNumber(date: scry.Expr<scry.DateOnly | null>): scry.Expr<number> {
  return call("DateDayNumber", date, [], returns("int")) as never;
}

/** The day of the week, numbered as .NET numbers it: 0 for Sunday. */
export function dayOfWeek(value: scry.Expr<Moment>): scry.Expr<"Sunday" | "Monday" | "Tuesday" | "Wednesday" | "Thursday" | "Friday" | "Saturday"> {
  return call("DateDayOfWeek", value, [], () => ({ value: { enum: "DayOfWeek" }, nullable: false })) as never;
}

/** The date a timestamp falls on, at midnight (`.Date`). */
export function dateOf<T extends scry.DateTime | scry.DateTimeOffset | null>(value: scry.Expr<T>): scry.Expr<T> {
  return call("DateDate", value, [], same) as never;
}

/** The time of day a timestamp carries, as the time since midnight (`.TimeOfDay`). */
export function timeOfDay(value: scry.Expr<scry.DateTime | scry.DateTimeOffset | null>): scry.Expr<scry.TimeSpan> {
  return call("DateTimeOfDay", value, [], returns("TimeSpan")) as never;
}

const span = (function_: KnownFunction) => (value: scry.Expr<scry.TimeSpan | null>): scry.Expr<number> =>
  call(function_, value, [], returns("int")) as never;

export const hours = span("TimeSpanHours");
export const minutes = span("TimeSpanMinutes");
export const seconds = span("TimeSpanSeconds");
export const milliseconds = span("TimeSpanMilliseconds");
export const microseconds = span("TimeSpanMicroseconds");
export const nanoseconds = span("TimeSpanNanoseconds");

/** The date half of a timestamp (`DateOnly.FromDateTime`). */
export function dateOnly(value: scry.Expr<scry.DateTime | null>): scry.Expr<scry.DateOnly> {
  return call("DateOnlyFromDateTime", value, [], returns("DateOnly")) as never;
}

/** The time half of a timestamp, or an elapsed time read as a time of day (`TimeOnly.From…`). */
export function timeOnly(value: scry.Expr<scry.DateTime | null> | scry.Expr<scry.TimeSpan | null>): scry.Expr<scry.TimeOnly> {
  if (scalarOf(expression(value, "timeOnly")) === "TimeSpan") {
    return call("TimeOnlyFromTimeSpan", value, [], returns("TimeOnly")) as never;
  }

  return call("TimeOnlyFromDateTime", value, [], returns("TimeOnly")) as never;
}

/** A date and a time of day composed into a timestamp (`DateOnly.ToDateTime`). */
export function dateTime(date: scry.Expr<scry.DateOnly | null>, time: scry.Expr<scry.TimeOnly | null>): scry.Expr<scry.DateTime> {
  return call("DateTimeFromDateAndTime", date, [[time, scalar("TimeOnly")]], returns("DateTime")) as never;
}

export function unixSeconds(value: scry.Expr<scry.DateTimeOffset | null>): scry.Expr<bigint> {
  return call("UnixSecondsFromOffset", value, [], returns("long")) as never;
}

export function unixMilliseconds(value: scry.Expr<scry.DateTimeOffset | null>): scry.Expr<bigint> {
  return call("UnixMillisecondsFromOffset", value, [], returns("long")) as never;
}

// A whole unit is an int on every temporal type; a fraction of one a double, but a date's days an
// int, since a DateOnly has no fraction of a day to add.
const add = (function_: KnownFunction, fractional: boolean) =>
  <T extends Moment | Clock>(value: scry.Expr<T>, amount: Operand<number>): scry.Expr<T> => {
    let type: Scalar = "int";
    if (fractional &&
        scalarOf(expression(value, function_)) !== "DateOnly") {
      type = "double";
    }

    return call(function_, value, [[amount, scalar(type)]], same) as never;
  };

export const addYears = add("DateAddYears", false);
export const addMonths = add("DateAddMonths", false);
export const addDays = add("DateAddDays", true);
export const addHours = add("DateAddHours", true);
export const addMinutes = add("DateAddMinutes", true);
export const addSeconds = add("DateAddSeconds", true);
export const addMilliseconds = add("DateAddMilliseconds", true);

// Numbers.

const keeps = (function_: KnownFunction) => <T extends Number_>(value: scry.Expr<T>): scry.Expr<T> =>
  call(function_, value, [], same) as never;

export const abs = keeps("MathAbs");
export const ceiling = keeps("MathCeiling");
export const floor = keeps("MathFloor");
export const truncate = keeps("MathTruncate");

export function round<T extends Number_>(value: scry.Expr<T>, digits?: Operand<number>): scry.Expr<T> {
  const args: [unknown, Argument][] = [];
  if (digits !== undefined) {
    args.push([digits, scalar("int")]);
  }

  return call("MathRound", value, args, same) as never;
}

/** -1, 0 or 1, as the value is negative, zero or positive. */
export function sign(value: scry.Expr<Number_>): scry.Expr<number> {
  return call("MathSign", value, [], returns("int")) as never;
}

const real = (function_: KnownFunction) => (value: scry.Expr<number | null>): scry.Expr<number> =>
  call(function_, value, [], returns("double")) as never;

export const sqrt = real("MathSqrt");
export const exp = real("MathExp");
export const log10 = real("MathLog10");
export const sin = real("MathSin");
export const cos = real("MathCos");
export const tan = real("MathTan");
export const asin = real("MathAsin");
export const acos = real("MathAcos");
export const atan = real("MathAtan");
export const degreesToRadians = real("MathDegreesToRadians");
export const radiansToDegrees = real("MathRadiansToDegrees");

/** The natural logarithm, or with `base` the logarithm to it. */
export function log(value: scry.Expr<number | null>, base?: Operand<number>): scry.Expr<number> {
  const args: [unknown, Argument][] = [];
  if (base !== undefined) {
    args.push([base, scalar("double")]);
  }

  return call("MathLog", value, args, returns("double")) as never;
}

export function pow(value: scry.Expr<number | null>, power: Operand<number>): scry.Expr<number> {
  return call("MathPow", value, [[power, scalar("double")]], returns("double")) as never;
}

/** The angle whose tangent is `y` over `x` (`Math.Atan2(y, x)`). */
export function atan2(y: scry.Expr<number | null>, x: Operand<number>): scry.Expr<number> {
  return call("MathAtan2", y, [[x, scalar("double")]], returns("double")) as never;
}

export function max<T extends Number_>(value: scry.Expr<T>, other: Operand<NoInfer<T>>): scry.Expr<T> {
  return call("MathMax", value, [[other, "target"]], same) as never;
}

export function min<T extends Number_>(value: scry.Expr<T>, other: Operand<NoInfer<T>>): scry.Expr<T> {
  return call("MathMin", value, [[other, "target"]], same) as never;
}

// Conversions: text parsed as a value, or a number widened to the named type.

const converts = <T>(function_: KnownFunction, type: Scalar) => (value: scry.Expr<unknown>): scry.Expr<T> =>
  call(function_, value, [], returns(type)) as never;

export const toInt32 = converts<number>("Int32From", "int");
export const toInt64 = converts<bigint>("Int64From", "long");
export const toDecimal = converts<scry.Decimal>("DecimalFrom", "decimal");
export const toDouble = converts<number>("DoubleFrom", "double");
export const toBoolean = converts<boolean>("BooleanFrom", "bool");
export const toByte = converts<number>("ByteFrom", "byte");
export const toInt16 = converts<number>("Int16From", "short");
export const toSingle = converts<number>("SingleFrom", "float");

// Bytes and enums.

/** The byte at `index`, or the default past the end. */
export function elementAt(bytes: scry.Expr<Bytes>, index: Operand<number>): scry.Expr<number> {
  return call("BytesElementAt", bytes, [[index, scalar("int")]], returns("byte")) as never;
}

/** Whether a [Flags] value carries all of `flags`' bits (`Enum.HasFlag`). */
export function hasFlag<V extends string>(value: scry.Expr<scry.Flags<V> | null>, flags: Plain<scry.Flags<V>>): scry.Expr<boolean> {
  return call("EnumHasFlag", value, [[flags, "target"]], returns("bool")) as never;
}
