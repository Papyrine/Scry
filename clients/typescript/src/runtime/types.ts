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

declare const collectionRow: unique symbol;

/**
 * A collection member. It cannot be read as a path: it is asked about through a subquery (any, all,
 * count, sum…) whose function is given each element as `E`, which is a model for a collection of
 * rows and an `Expr` for a collection of values. `R` is a row of the collection's model, which a query
 * flattening it returns.
 */
export interface Collection<E, R = never> {
  readonly [collection]: E;
  readonly [collectionRow]?: R;
  /** The elements a question is about, or a returned collection holds, narrowed once. */
  where(predicate: (element: E) => Condition): Collection<E, R>;
  any(predicate?: (element: E) => Condition): Expr<boolean>;
  all(predicate: (element: E) => Condition): Expr<boolean>;
  count(predicate?: (element: E) => Condition): Expr<number>;
  /** Whether a collection of values holds the value. */
  contains(value: ElementOf<E> | Expr<ElementOf<E>>): Expr<boolean>;
  /** The selector reads each element; over a collection of values it may be left out. */
  sum<T = ElementOf<E>>(selector?: (element: E) => Expr<T>): Expr<T>;
  average<T = ElementOf<E>>(selector?: (element: E) => Expr<T>): Expr<Average<T>>;
  min<T = ElementOf<E>>(selector?: (element: E) => Expr<T>): Expr<T>;
  max<T = ElementOf<E>>(selector?: (element: E) => Expr<T>): Expr<T>;
  /** The collection returned in a row: what of each element, a value or a flat object of values. */
  select<P>(selector: (element: E) => P): CollectionProjection<Shape<P>>;
}

type Condition = Expr<boolean> | Expr<boolean | null>;
type ElementOf<E> = E extends Expr<infer T> ? T : never;
type Average<T> = T extends Decimal ? Decimal : number;

declare const projected: unique symbol;

/** A collection returned in a row, each element as `T`. */
export interface CollectionProjection<T> {
  readonly [projected]: T;
}

/** What a projection returns, from the object a `select` function builds. */
export type Shape<P> =
  P extends Expr<infer T> ? T :
  P extends CollectionProjection<infer E> ? E[] :
  P extends Collection<Expr<infer T>, never> ? T[] :
  P extends Date | Uint8Array ? P :
  P extends object ? { -readonly [K in keyof P]: Shape<P[K]> } :
  P;

/** The rows of one group, folded: what a grouped query's condition and projection read. */
export interface Group<Q> {
  count(predicate?: (row: Q) => Condition): Expr<number>;
  sum<T>(selector: (row: Q) => Expr<T>): Expr<T>;
  average<T>(selector: (row: Q) => Expr<T>): Expr<Average<T>>;
  min<T>(selector: (row: Q) => Expr<T>): Expr<T>;
  max<T>(selector: (row: Q) => Expr<T>): Expr<T>;
  /** One value of every row, to fold: made distinct, then counted, summed or joined. */
  values<T>(selector: (row: Q) => Expr<T>): GroupValues<T>;
}

/** A group of rows and the key it was made by: the key's own expression, or an object of them. */
export interface Grouping<Q, K> extends Group<Q> {
  readonly key: K;
}

export interface GroupValues<T> {
  distinct(): GroupValues<T>;
  count(): Expr<number>;
  sum(): Expr<T>;
  average(): Expr<Average<T>>;
  min(): Expr<T>;
  max(): Expr<T>;
  /** The values as one string, `separator` between them (string.Join). */
  join(separator: string): Expr<string | null>;
}

/**
 * A navigation that may be absent. Every value read through it may be null, however the member at the
 * end is declared, since the row it is read from may not be there.
 */
export type Optional<M> = {
  readonly [K in keyof M]: M[K] extends Expr<infer T>
    ? Expr<T | null>
    : M[K] extends Collection<unknown, unknown>
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
 * A date and time with no offset, as text. A request writes it in the round-trip form,
 * `2026-09-03T00:00:00.0000000` with a trailing `Z` where it is UTC; a row carries it as the server's
 * serializer writes it, `2026-09-03T00:00:00`, a fraction only where there is one. A wall clock
 * rather than an instant, which is why it is not a `Date`: reading one as a `Date` would place it in
 * the reader's zone.
 */
export type DateTime = Branded<string, "DateTime">;

/** A date, as `2026-09-03`. */
export type DateOnly = Branded<string, "DateOnly">;

/** A time of day: `05:06:07.1230000` in a request, `05:06:07.123` in a row. */
export type TimeOnly = Branded<string, "TimeOnly">;

/** An elapsed time, as `01:02:03.4560000`, with a day count in front past a day and a fraction only where there is one. */
export type TimeSpan = Branded<string, "TimeSpan">;

/** A date and time with its offset: `2026-03-04T05:06:07.1230000+02:00` in a request, the fraction only where there is one in a row. */
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
