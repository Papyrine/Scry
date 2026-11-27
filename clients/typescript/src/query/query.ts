// A query: a source and the operators applied to it, typed by the states the server's pipeline grammar
// moves through, so an ordering the server would refuse does not compile. Each operator returns a new
// query; a terminal builds the request and sends it.

import type {
  InSourceNode,
  JoinMember,
  JoinOp,
  Node,
  QueryOp,
  QueryRequest,
  QueryResponse,
  ResultKind,
  SetKind,
  SetOp
} from "../wire.ts";
import type { ScrySchema } from "../runtime/schema.ts";
import type * as scry from "../runtime/types.ts";
import { requiredVersion } from "../request/version.ts";
import { Collection, Expression, Grouping, Scope, groupKey, row, type GroupKey, type Side } from "./expression.ts";
import { projectionOf, wireName } from "./projection.ts";

/** Sends a request and answers with the server's response. */
export interface Transport {
  query(request: QueryRequest): Promise<QueryResponse>;
}

export interface ScryOptions {
  readonly transport?: Transport;
  /**
   * What a query projecting nothing asks for. `"model"`, the default, projects the model's scalar
   * members, as a generated C# client does; `"server"` leaves it to the server, as a C# source built
   * by hand does.
   */
  readonly defaultProjection?: "model" | "server";
  /** Whether each request carries the description's schema stamp, as a generated C# client's do. */
  readonly stamp?: boolean;
}

/** A page of rows, and what resumes after it. */
export interface Page<R> {
  readonly items: R[];
  readonly hasMore: boolean;
  readonly cursor?: string;
}

type Shape<P> = scry.Shape<P>;

type Condition = scry.Expr<boolean> | scry.Expr<boolean | null>;
type Average<T> = T extends scry.Decimal ? scry.Decimal : number;

type QueryOf<T> = T extends scry.SourceTypes<infer Q, unknown> ? Q : never;
type RowOf<T> = T extends scry.SourceTypes<unknown, infer R> ? R : never;

/** What every query can end with. */
export interface Final<R> {
  toList(): Promise<R[]>;
  first(): Promise<R>;
  firstOrDefault(): Promise<R | null>;
  single(): Promise<R>;
  singleOrDefault(): Promise<R | null>;
  last(): Promise<R>;
  lastOrDefault(): Promise<R | null>;
  elementAt(index: number): Promise<R>;
  count(): Promise<number>;
  longCount(): Promise<bigint>;
  any(): Promise<boolean>;
  page(size?: number, cursor?: string): Promise<Page<R>>;
  /** The request a list of the rows sends, without sending it. */
  toRequest(): QueryRequest;
}

/** Rows of a model, before anything reshapes them. */
export interface Rows<Q, R, S> extends Final<R> {
  where(predicate: (_: Q) => Condition): Rows<Q, R, S>;
  orderBy(key: (_: Q) => scry.Expr<unknown>): Ordered<Q, R, S>;
  orderByDescending(key: (_: Q) => scry.Expr<unknown>): Ordered<Q, R, S>;
  skip(count: number): Rows<Q, R, S>;
  take(count: number): Rows<Q, R, S>;
  /** The rows that are of a derived source, read as it. */
  ofType<K extends keyof S>(source: K): Rows<QueryOf<S[K]>, RowOf<S[K]>, S>;
  /** The elements of a collection each row holds, as the rows. */
  selectMany<E, ER>(collection: (_: Q) => scry.Collection<E, ER>): Rows<E, ER, S>;
  groupBy<K>(key: (_: Q) => K): Grouped<Q, K>;
  select<P>(projection: (_: Q) => P): Projected<Shape<P>, S>;
  distinct(): Final<R>;
  join<IQ, IR, K, P>(inner: Rows<IQ, IR, S>, outerKey: (_: Q) => K, innerKey: (_: IQ) => K, result: (outer: Q, inner: IQ) => P): Final<Shape<P>>;
  leftJoin<IQ, IR, K, P>(inner: Rows<IQ, IR, S>, outerKey: (_: Q) => K, innerKey: (_: IQ) => K, result: (outer: Q, inner: scry.Optional<IQ>) => P): Final<Shape<P>>;
  rightJoin<IQ, IR, K, P>(inner: Rows<IQ, IR, S>, outerKey: (_: Q) => K, innerKey: (_: IQ) => K, result: (outer: scry.Optional<Q>, inner: IQ) => P): Final<Shape<P>>;
  groupJoin<IQ, IR, K, P>(inner: Rows<IQ, IR, S>, outerKey: (_: Q) => K, innerKey: (_: IQ) => K, result: (outer: Q, inner: scry.Group<IQ>) => P): Final<Shape<P>>;
  first(predicate?: (_: Q) => Condition): Promise<R>;
  firstOrDefault(predicate?: (_: Q) => Condition): Promise<R | null>;
  single(predicate?: (_: Q) => Condition): Promise<R>;
  singleOrDefault(predicate?: (_: Q) => Condition): Promise<R | null>;
  last(predicate?: (_: Q) => Condition): Promise<R>;
  lastOrDefault(predicate?: (_: Q) => Condition): Promise<R | null>;
  count(predicate?: (_: Q) => Condition): Promise<number>;
  longCount(predicate?: (_: Q) => Condition): Promise<bigint>;
  any(predicate?: (_: Q) => Condition): Promise<boolean>;
  all(predicate: (_: Q) => Condition): Promise<boolean>;
  sum<T>(selector: (_: Q) => scry.Expr<T>): Promise<T>;
  average<T>(selector: (_: Q) => scry.Expr<T>): Promise<Average<T>>;
  min<T>(selector: (_: Q) => scry.Expr<T>): Promise<T>;
  max<T>(selector: (_: Q) => scry.Expr<T>): Promise<T>;
  /** The row with the smallest key. */
  minBy(key: (_: Q) => scry.Expr<unknown>): Promise<R>;
  /** The row with the largest key. */
  maxBy(key: (_: Q) => scry.Expr<unknown>): Promise<R>;
}

/** Rows straight after an ordering, which a further ordering refines. */
export interface Ordered<Q, R, S> extends Rows<Q, R, S> {
  thenBy(key: (_: Q) => scry.Expr<unknown>): Ordered<Q, R, S>;
  thenByDescending(key: (_: Q) => scry.Expr<unknown>): Ordered<Q, R, S>;
  reverse(): Rows<Q, R, S>;
}

/** Rows grouped by a key: filtered by what they fold to, then projected, which is required. */
export interface Grouped<Q, K> {
  where(having: (group: scry.Grouping<Q, K>) => Condition): Grouped<Q, K>;
  select<P>(projection: (group: scry.Grouping<Q, K>) => P): Projected<Shape<P>, never>;
}

/** Projected rows: deduplicated, combined with another source's, or ended. */
export interface Projected<R, S> extends Final<R> {
  distinct(): Final<R>;
  union(other: Projected<R, S>): Final<R>;
  concat(other: Projected<R, S>): Final<R>;
  intersect(other: Projected<R, S>): Final<R>;
  except(other: Projected<R, S>): Final<R>;
}

/** Every source of a description, as the root of a query. */
export type Scry<S> = {
  readonly [K in keyof S]: S[K] extends scry.SourceTypes<infer Q, infer R> ? Rows<Q, R, S> : never;
};

class Client {
  readonly schema: ScrySchema;
  readonly options: ScryOptions;
  readonly scope: Scope;

  constructor(schema: ScrySchema, options: ScryOptions) {
    this.schema = schema;
    this.options = options;
    this.scope = new Scope(schema);
  }
}

type Stage = "rows" | "ordered" | "grouped" | "projected" | "final";

interface State {
  readonly client: Client;
  /** The source's name, as the request's root. */
  readonly root: string;
  /** The model the rows are, while they are rows of one. */
  readonly model: string | undefined;
  readonly ops: readonly QueryOp[];
  readonly stage: Stage;
  readonly group: GroupKey | undefined;
  /** It reads a member the model marks sensitive. */
  readonly sensitive: boolean;
}

export function createScry<S>(schema: ScrySchema, options: ScryOptions = {}): Scry<S> {
  const client = new Client(schema, options);
  const sources: Record<string, unknown> = {};
  for (const [name, source] of Object.entries(schema.sources)) {
    Object.defineProperty(sources, name, {
      enumerable: true,
      get: () => new Query({
        client,
        root: source.name,
        model: source.model,
        ops: [],
        stage: "rows",
        group: undefined,
        sensitive: false
      })
    });
  }

  return sources as Scry<S>;
}

const sideOps = "only filters, and an ordering bounded by skip() or take(), cross over, in that order";

class Query {
  readonly #state: State;

  constructor(state: State) {
    this.#state = state;
  }

  /** @internal */
  get state(): State {
    return this.#state;
  }

  #next(op: QueryOp, stage: Stage, change: Partial<State> = {}, sensitive = false): Query {
    return new Query({
      ...this.#state,
      ...change,
      ops: [...this.#state.ops, op],
      stage,
      sensitive: this.#state.sensitive || sensitive
    });
  }

  #expect(stages: readonly Stage[], what: string): void {
    if (!stages.includes(this.#state.stage)) {
      throw new Error(`${what} cannot follow what this query has already done.`);
    }
  }

  #row(side?: Side, optional = false): object {
    const model = this.#state.model;
    if (model === undefined) {
      throw new Error("The query's rows are no longer a model's.");
    }

    return row(this.#state.client.scope, model, [], optional, side);
  }

  #expression(function_: (_: never) => unknown, what: string): Expression {
    const result = (function_ as (_: unknown) => unknown)(this.#row());
    if (!(result instanceof Expression)) {
      throw new Error(`${what} has to be an expression, and this one is ${typeof result}. JavaScript's own !, && and || ask nothing of the server: use not(), and() and or().`);
    }

    return result;
  }

  where(predicate: (_: never) => unknown): Query {
    if (this.#state.stage === "grouped") {
      const group = this.#state.group!;
      const having = (predicate as (_: unknown) => unknown)(new Grouping(this.#state.client.scope, this.#state.model!, group));
      if (!(having instanceof Expression)) {
        throw new Error("A grouped query's condition has to be an expression.");
      }

      return this.#next({ $type: "where", predicate: having.node }, "grouped", {}, having.sensitive);
    }

    this.#expect(["rows", "ordered"], "where()");
    const condition = this.#expression(predicate, "A condition");
    return this.#next({ $type: "where", predicate: condition.node }, "rows", {}, condition.sensitive);
  }

  #order(key: (_: never) => unknown, first: boolean, descending: boolean): Query {
    const ordered = this.#expression(key, "An ordering's key");
    if (first) {
      this.#expect(["rows", "ordered"], "orderBy()");
      return this.#next({ $type: "orderBy", key: ordered.node, descending }, "ordered", {}, ordered.sensitive);
    }

    this.#expect(["ordered"], "thenBy()");
    return this.#next({ $type: "thenBy", key: ordered.node, descending }, "ordered", {}, ordered.sensitive);
  }

  orderBy(key: (_: never) => unknown): Query {
    return this.#order(key, true, false);
  }

  orderByDescending(key: (_: never) => unknown): Query {
    return this.#order(key, true, true);
  }

  thenBy(key: (_: never) => unknown): Query {
    return this.#order(key, false, false);
  }

  thenByDescending(key: (_: never) => unknown): Query {
    return this.#order(key, false, true);
  }

  reverse(): Query {
    this.#expect(["ordered"], "reverse()");
    return this.#next({ $type: "reverse" }, "rows");
  }

  skip(count: number): Query {
    this.#expect(["rows", "ordered"], "skip()");
    return this.#next({ $type: "skip", count }, "rows");
  }

  take(count: number): Query {
    this.#expect(["rows", "ordered"], "take()");
    return this.#next({ $type: "take", count }, "rows");
  }

  ofType(source: string): Query {
    this.#expect(["rows", "ordered"], "ofType()");
    const described = this.#state.client.schema.sources[source];
    if (described === undefined) {
      throw new Error(`'${source}' is not a source.`);
    }

    return this.#next({ $type: "ofType", type: described.name }, "rows", { model: described.model });
  }

  selectMany(collection: (_: never) => unknown): Query {
    this.#expect(["rows", "ordered"], "selectMany()");
    const flattened = (collection as (_: unknown) => unknown)(this.#row());
    if (!(flattened instanceof Collection) ||
        !("model" in flattened.element) ||
        flattened.filter !== undefined) {
      throw new Error("selectMany() flattens a collection of rows the row holds, as it is: filter the rows after it.");
    }

    return this.#next({ $type: "selectMany", path: [...flattened.path] }, "rows", { model: flattened.element.model }, flattened.sensitive);
  }

  groupBy(key: (_: never) => unknown): Query {
    this.#expect(["rows", "ordered"], "groupBy()");
    const grouped = groupKey(this.#state.client.scope, (key as (_: unknown) => unknown)(this.#row()));
    return this.#next({ $type: "groupBy", keys: [...grouped.keys] }, "grouped", { group: grouped }, grouped.sensitive);
  }

  select(projection: (_: never) => unknown): Query {
    const scope = this.#state.client.scope;
    if (this.#state.stage === "grouped") {
      const grouping = new Grouping(scope, this.#state.model!, this.#state.group!);
      const built = projectionOf((projection as (_: unknown) => unknown)(grouping), true, scope);
      return this.#next({ $type: "select", projection: built.projection }, "projected", { model: undefined }, built.sensitive);
    }

    this.#expect(["rows", "ordered"], "select()");
    const built = projectionOf((projection as (_: unknown) => unknown)(this.#row()), false, scope);
    return this.#next({ $type: "select", projection: built.projection }, "projected", { model: undefined }, built.sensitive);
  }

  distinct(): Query {
    this.#expect(["rows", "ordered", "projected"], "distinct()");
    return this.#next({ $type: "distinct" }, "final");
  }

  join(inner: Rows<unknown, unknown, unknown>, outerKey: (_: never) => unknown, innerKey: (_: never) => unknown, result: (outer: never, inner: never) => unknown): Query {
    return this.#join("Inner", inner, outerKey, innerKey, result);
  }

  leftJoin(inner: Rows<unknown, unknown, unknown>, outerKey: (_: never) => unknown, innerKey: (_: never) => unknown, result: (outer: never, inner: never) => unknown): Query {
    return this.#join("Left", inner, outerKey, innerKey, result);
  }

  rightJoin(inner: Rows<unknown, unknown, unknown>, outerKey: (_: never) => unknown, innerKey: (_: never) => unknown, result: (outer: never, inner: never) => unknown): Query {
    return this.#join("Right", inner, outerKey, innerKey, result);
  }

  groupJoin(inner: Rows<unknown, unknown, unknown>, outerKey: (_: never) => unknown, innerKey: (_: never) => unknown, result: (outer: never, inner: never) => unknown): Query {
    return this.#join("Group", inner, outerKey, innerKey, result);
  }

  #join(kind: JoinOp["kind"], innerQuery: unknown, outerKey: (_: never) => unknown, innerKey: (_: never) => unknown, result: (outer: never, inner: never) => unknown): Query {
    this.#expect(["rows", "ordered"], "A join");
    const inner = asQuery(innerQuery, "The inner side of a join");
    const innerModel = inner.state.model;
    if (innerModel === undefined ||
        (inner.state.stage !== "rows" && inner.state.stage !== "ordered")) {
      throw new Error(`The inner side of a join is rows of a source: ${sideOps}.`);
    }

    const scope = this.#state.client.scope;
    const outerRow = this.#row("Outer", kind === "Right");
    const innerRow = row(scope, innerModel, [], kind === "Left", "Inner");
    const outer = joinKey((outerKey as (_: unknown) => unknown)(outerRow));
    const innerJoined = joinKey((innerKey as (_: unknown) => unknown)(innerRow));

    let built: unknown;
    if (kind === "Group") {
      const group = new Grouping(scope, innerModel, { keys: [], key: undefined, sensitive: false });
      built = (result as (outer: unknown, inner: unknown) => unknown)(outerRow, group);
    } else {
      built = (result as (outer: unknown, inner: unknown) => unknown)(outerRow, innerRow);
    }

    const [members, sensitive] = joinMembers(built, kind);
    const side = sidePipeline(inner.state.ops, "the inner side of a join");
    const op: JoinOp = {
      $type: "join",
      root: inner.state.root,
      kind,
      outerKey: outer.node,
      innerKey: innerJoined.node,
      ...(side.predicate === undefined ? {} : { innerPredicate: side.predicate }),
      result: members,
      ...(side.ops === undefined ? {} : { innerOps: side.ops })
    };
    return this.#next(op, "final", { model: undefined }, sensitive || outer.sensitive || innerJoined.sensitive || inner.state.sensitive);
  }

  #combine(kind: SetKind, otherQuery: unknown): Query {
    this.#expect(["projected"], `${kind.toLowerCase()}()`);
    const other = asQuery(otherQuery, `The other side of ${kind.toLowerCase()}()`);
    const ops = other.state.ops;
    const last = ops.at(-1);
    if (other.state.stage !== "projected" ||
        last?.$type !== "select") {
      throw new Error(`The other side of ${kind.toLowerCase()}() selects the shape both sides share, last.`);
    }

    const side = sidePipeline(ops.slice(0, -1), `the other side of ${kind.toLowerCase()}()`);
    const op: SetOp = {
      $type: "set",
      kind,
      root: other.state.root,
      ...(side.predicate === undefined ? {} : { predicate: side.predicate }),
      projection: last.projection,
      ...(side.ops === undefined ? {} : { operandOps: side.ops })
    };
    return this.#next(op, "final", {}, other.state.sensitive);
  }

  union(other: unknown): Query {
    return this.#combine("Union", other);
  }

  concat(other: unknown): Query {
    return this.#combine("Concat", other);
  }

  intersect(other: unknown): Query {
    return this.#combine("Intersect", other);
  }

  except(other: unknown): Query {
    return this.#combine("Except", other);
  }

  // Terminals.

  toRequest(): QueryRequest {
    return this.#request([]);
  }

  toList(): Promise<unknown> {
    return this.#send([], "List");
  }

  #element(op: "first" | "single" | "last", orDefault: boolean, predicate: ((_: never) => unknown) | undefined): Promise<unknown> {
    let terminal: QueryOp = { $type: op, orDefault };
    if (predicate !== undefined) {
      this.#expect(["rows", "ordered"], `A condition on ${op}()`);
      terminal = { $type: op, orDefault, predicate: this.#expression(predicate, "A condition").node };
    }

    return this.#send([terminal], "Single");
  }

  first(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#element("first", false, predicate);
  }

  firstOrDefault(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#element("first", true, predicate);
  }

  single(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#element("single", false, predicate);
  }

  singleOrDefault(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#element("single", true, predicate);
  }

  last(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#element("last", false, predicate);
  }

  lastOrDefault(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#element("last", true, predicate);
  }

  elementAt(index: number): Promise<unknown> {
    return this.#send([{ $type: "skip", count: index }, { $type: "first", orDefault: false }], "Single");
  }

  minBy(key: (_: never) => unknown): Promise<unknown> {
    return this.orderBy(key).first();
  }

  maxBy(key: (_: never) => unknown): Promise<unknown> {
    return this.orderByDescending(key).first();
  }

  #scalar(op: "count" | "longCount" | "any" | "all", predicate: ((_: never) => unknown) | undefined): Promise<unknown> {
    if (predicate === undefined) {
      if (op === "all") {
        throw new Error("all() asks whether every row meets a condition, so it needs one.");
      }

      return this.#send([{ $type: op }], "Scalar");
    }

    this.#expect(["rows", "ordered"], `A condition on ${op}()`);
    const condition = this.#expression(predicate, "A condition");
    return this.#send([{ $type: op, predicate: condition.node }], "Scalar", condition.sensitive);
  }

  count(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#scalar("count", predicate);
  }

  longCount(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#scalar("longCount", predicate);
  }

  any(predicate?: (_: never) => unknown): Promise<unknown> {
    return this.#scalar("any", predicate);
  }

  all(predicate: (_: never) => unknown): Promise<unknown> {
    return this.#scalar("all", predicate);
  }

  #fold(function_: "Sum" | "Average" | "Min" | "Max", selector: (_: never) => unknown): Promise<unknown> {
    this.#expect(["rows", "ordered"], `${function_.toLowerCase()}()`);
    const selected = this.#expression(selector, "What to fold");
    return this.#send([{ $type: "aggregate", function: function_, selector: selected.node }], "Scalar", selected.sensitive);
  }

  sum(selector: (_: never) => unknown): Promise<unknown> {
    return this.#fold("Sum", selector);
  }

  average(selector: (_: never) => unknown): Promise<unknown> {
    return this.#fold("Average", selector);
  }

  min(selector: (_: never) => unknown): Promise<unknown> {
    return this.#fold("Min", selector);
  }

  max(selector: (_: never) => unknown): Promise<unknown> {
    return this.#fold("Max", selector);
  }

  page(size?: number, cursor?: string): Promise<unknown> {
    return this.#send([{
      $type: "page",
      ...(size === undefined ? {} : { size }),
      ...(cursor === undefined ? {} : { cursor })
    }], "Page");
  }

  /** @internal Whether the request reads a member the model marks sensitive. */
  get sensitive(): boolean {
    return this.#state.sensitive;
  }

  #request(terminal: readonly QueryOp[]): QueryRequest {
    const pipeline = [...this.#state.ops];
    const last = terminal.at(-1);
    const leading = terminal.slice(0, -1);
    pipeline.push(...leading);
    this.#addDefaultProjection(pipeline, last);
    if (last !== undefined) {
      pipeline.push(last);
    }

    const { client } = this.#state;
    const request: QueryRequest = { version: requiredVersion(pipeline), root: this.#state.root, pipeline };
    if (client.options.stamp === false) {
      return request;
    }

    return { ...request, stamp: client.schema.stamp };
  }

  // A query writing no projection asks for the model's scalar members by name, as the C# client's
  // AddDefaultProjection does, so the rows come back keyed by the names this client knows.
  #addDefaultProjection(pipeline: QueryOp[], terminal: QueryOp | undefined): void {
    const { client, model } = this.#state;
    if (client.options.defaultProjection === "server" ||
        model === undefined) {
      return;
    }

    const members = client.scope.model(model).defaultProjection;
    if (members.length === 0 ||
        terminal?.$type === "count" ||
        terminal?.$type === "longCount" ||
        terminal?.$type === "any" ||
        terminal?.$type === "all" ||
        terminal?.$type === "aggregate" ||
        ((terminal?.$type === "first" || terminal?.$type === "single" || terminal?.$type === "last") && terminal.predicate !== undefined) ||
        pipeline.some(_ => _.$type === "select" || _.$type === "groupBy" || _.$type === "join" || _.$type === "set")) {
      return;
    }

    pipeline.push({
      $type: "select",
      projection: { members: members.map(_ => ({ name: _, value: { $type: "node", node: { $type: "member", path: [_] } } })) }
    });
  }

  async #send(terminal: readonly QueryOp[], kind: ResultKind, sensitive = false): Promise<unknown> {
    const request = this.#request(terminal);
    const transport = this.#state.client.options.transport;
    if (transport === undefined) {
      throw new Error("This client has no transport to send its requests with.");
    }

    void sensitive;
    const response = await transport.query(request);
    if (response.kind !== kind) {
      throw new Error(`The server answered a ${kind} query with a ${response.kind}.`);
    }

    return response.payload;
  }
}

function asQuery(value: unknown, what: string): Query {
  if (!(value instanceof Query)) {
    throw new Error(`${what} is a query of this client's.`);
  }

  return value;
}

// A key built of several members joins on all of them, position by position; one of one is the member.
function joinKey(value: unknown): Expression {
  if (value instanceof Expression) {
    return value;
  }

  if (typeof value !== "object" ||
      value === null) {
    throw new Error("A join key is an expression, or an object of expressions joining on all of them.");
  }

  const parts = Object.values(value);
  for (const part of parts) {
    if (!(part instanceof Expression)) {
      throw new Error("Every member of a composite join key is an expression.");
    }
  }

  const expressions = parts as Expression[];
  if (expressions.length === 0) {
    throw new Error("A composite join key needs at least one member.");
  }

  if (expressions.length === 1) {
    return expressions[0]!;
  }

  return new Expression(
    { $type: "compositeKey", parts: expressions.map(_ => _.node) },
    { value: undefined, nullable: false },
    expressions[0]!.scope,
    expressions.some(_ => _.sensitive));
}

// A join's result names a member of either row, or over a group join an aggregate of the group.
function joinMembers(value: unknown, kind: JoinOp["kind"]): [JoinMember[], boolean] {
  if (typeof value !== "object" ||
      value === null ||
      value instanceof Expression) {
    throw new Error("A join's result is an object, each of its members read off the outer or inner row.");
  }

  const members: JoinMember[] = [];
  let sensitive = false;
  for (const [key, member] of Object.entries(value)) {
    if (!(member instanceof Expression)) {
      throw new Error(`The join's '${key}' is not read off either row.`);
    }

    sensitive ||= member.sensitive;
    const name = wireName(key);
    if (kind === "Group" &&
        member.node.$type === "aggregate") {
      const { $type: _, ...aggregate } = member.node;
      members.push({ name, side: "Inner", path: [], aggregate });
      continue;
    }

    if (member.node.$type !== "member" ||
        member.side === undefined) {
      throw new Error(`The join's '${key}' has to be a member path on the outer or inner row.`);
    }

    members.push({ name, side: member.side, path: member.node.path });
  }

  if (members.length === 0) {
    throw new Error("A join must project at least one member.");
  }

  return [members, sensitive];
}

// The other side of a join or a set operation: its filters cross as one predicate every server reads,
// and anything more as a pipeline of its own: Where* [OrderBy ThenBy* (Skip [Take] | Take)].
function sidePipeline(ops: readonly QueryOp[], side: string): { predicate?: Node; ops?: QueryOp[] } {
  if (ops.every(_ => _.$type === "where")) {
    let predicate: Node | undefined;
    for (const op of ops) {
      if (op.$type !== "where") {
        continue;
      }

      if (predicate === undefined) {
        predicate = op.predicate;
      } else {
        predicate = { $type: "binary", op: "AndAlso", left: predicate, right: op.predicate };
      }
    }

    if (predicate === undefined) {
      return {};
    }

    return { predicate };
  }

  let stage = 0;
  for (const op of ops) {
    if (op.$type === "where" && stage === 0) {
      continue;
    }

    if (op.$type === "orderBy" && stage === 0) {
      stage = 1;
      continue;
    }

    if (op.$type === "thenBy" && stage === 1) {
      continue;
    }

    if (op.$type === "skip" && stage === 1) {
      stage = 2;
      continue;
    }

    if (op.$type === "take" && (stage === 1 || stage === 2)) {
      stage = 3;
      continue;
    }

    throw new Error(`'${op.$type}' is not supported on ${side}: ${sideOps}.`);
  }

  if (stage === 1) {
    throw new Error(`An ordering on ${side} must be bounded by skip() or take(): unbounded, a subquery discards it.`);
  }

  return { ops: [...ops] };
}

/** A value a source holds where its filter does, as a set to look a value up in. */
export function inSource(value: Expression, sourceQuery: unknown, selector: (_: never) => unknown): Expression {
  const source = asQuery(sourceQuery, "The source a value is looked up in");
  const { model, ops, root, client } = source.state;
  if (model === undefined ||
      ops.some(_ => _.$type !== "where")) {
    throw new Error("The source a value is looked up in carries filters only.");
  }

  const selected = (selector as (_: unknown) => unknown)(row(client.scope, model, [], false));
  if (!(selected instanceof Expression)) {
    throw new Error("What a source's rows are looked up by is an expression.");
  }

  const side = sidePipeline(ops, "a source a value is looked up in");
  const node: InSourceNode = {
    $type: "inSource",
    value: value.node,
    root,
    selector: selected.node,
    ...(side.predicate === undefined ? {} : { predicate: side.predicate })
  };
  return new Expression(node, { value: { scalar: "bool" }, nullable: false }, value.scope, value.sensitive || selected.sensitive || source.state.sensitive);
}
