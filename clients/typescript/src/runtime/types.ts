// The types generated code is written against. A query is captured, never run on the client, so the
// members of a query model are not values: each is an expression standing for the value the server
// will read, and the types below say what that value is. Everything here is a type; none of it exists
// at run time.

declare const expression: unique symbol;
declare const collection: unique symbol;
declare const brand: unique symbol;
declare const flags: unique symbol;

/**
 * A value inside a query: a member read, a constant, or something computed from them. `T` is the
 * value's type as a row delivers it. An expression is never the value itself, so comparing one with
 * `>` or `===` asks nothing of the server; the operator functions do.
 */
export interface Expr<T> {
  readonly [expression]: T;
}

/**
 * A collection member. It cannot be read as a path: it is asked about through a subquery (any, all,
 * count, sum…) whose function is given each element as `E`, which is a model for a collection of
 * rows and an `Expr` for a collection of values.
 */
export interface Collection<E> {
  readonly [collection]: E;
}

/**
 * A navigation that may be absent. Every value read through it may be null, however the member at the
 * end is declared, since the row it is read from may not be there.
 */
export type Optional<M> = {
  readonly [K in keyof M]: M[K] extends Expr<infer T>
    ? Expr<T | null>
    : M[K] extends Collection<unknown>
      ? M[K]
      : Optional<M[K]>;
};

type Branded<T, B extends string> = T & { readonly [brand]: B };

/**
 * A decimal, as its invariant text. A JavaScript number holds about sixteen significant digits and a
 * decimal up to twenty-nine, so a decimal is never read as one.
 */
export type Decimal = Branded<string, "Decimal">;

/** A Guid, as its text. */
export type Guid = Branded<string, "Guid">;

/**
 * A date and time with no offset, as its round-trip text: `2026-09-03T00:00:00.0000000`, with a
 * trailing `Z` where it is UTC. A wall clock rather than an instant, which is why it is not a `Date`:
 * reading one as a `Date` would place it in the reader's zone.
 */
export type DateTime = Branded<string, "DateTime">;

/** A date, as `2026-09-03`. */
export type DateOnly = Branded<string, "DateOnly">;

/** A time of day, as `05:06:07.1230000`. */
export type TimeOnly = Branded<string, "TimeOnly">;

/** An elapsed time, as `01:02:03.4560000`, with a day count in front past a day. */
export type TimeSpan = Branded<string, "TimeSpan">;

/** A date and time with its offset, as `2026-03-04T05:06:07.1230000+02:00`. */
export type DateTimeOffset = Branded<string, "DateTimeOffset">;

/**
 * A value of a [Flags] enum: its names joined by `, ` as .NET writes them, such as `"Parking, Gym"`.
 * `V` is the enum's names, any combination of which is a value.
 */
export type Flags<V extends string> = Branded<string, "Flags"> & { readonly [flags]?: V };

/** The two types a source is written and read with: the model a query reads, and a default row. */
export interface SourceTypes<Q, R> {
  readonly query: Q;
  readonly row: R;
}

/** The payload a command is sent with, and the result it completes with (`void` where none). */
export interface CommandTypes<P, R> {
  readonly payload: P;
  readonly result: R;
}
