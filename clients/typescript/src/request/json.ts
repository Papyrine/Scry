// Writes a request as the server's serializer would: the same member order, the same escaping and the
// same spelling of a path and of a projection member, so a request built here is the byte-string a C#
// client sends for the same query. The cache, the URL form and the audit all key off those bytes.

import type {
  AggregateNode,
  AttachmentKey,
  AttachmentRequest,
  CommandRequest,
  JoinMember,
  Node,
  Path,
  Projection,
  ProjectionEntry,
  ProjectionValue,
  QueryBatchRequest,
  QueryOp,
  QueryRequest
} from "../wire.ts";

const backslash = String.fromCharCode(92);

// What System.Text.Json's default encoder writes as two characters.
const short: ReadonlyMap<number, string> = new Map([
  [0x08, `${backslash}b`],
  [0x09, `${backslash}t`],
  [0x0a, `${backslash}n`],
  [0x0c, `${backslash}f`],
  [0x0d, `${backslash}r`],
  [0x5c, `${backslash}${backslash}`]
]);

// The printable ASCII the default encoder escapes anyway: the quote, and what is sensitive in HTML.
const sensitive: ReadonlySet<number> = new Set([0x22, 0x26, 0x27, 0x2b, 0x3c, 0x3e, 0x60]);

function escaped(code: number): string {
  return `${backslash}u${code.toString(16).toUpperCase().padStart(4, "0")}`;
}

/**
 * A JSON string as the server's default encoder writes it: printable ASCII as it is, except the quote
 * and `& ' + < > \``; the five control characters JSON names as two characters; everything else as
 * `\uXXXX` in uppercase hex, a pair of surrogates as two, and a lone surrogate as U+FFFD.
 */
export function quote(text: string): string {
  let out = "\"";
  for (let index = 0; index < text.length; index++) {
    const code = text.charCodeAt(index);
    if (code >= 0x20 &&
        code < 0x7f &&
        code !== 0x5c &&
        !sensitive.has(code)) {
      out += text.charAt(index);
      continue;
    }

    const pair = short.get(code);
    if (pair !== undefined) {
      out += pair;
      continue;
    }

    if (code >= 0xd800 &&
        code <= 0xdbff) {
      const next = text.charCodeAt(index + 1);
      if (next >= 0xdc00 &&
          next <= 0xdfff) {
        out += escaped(code) + escaped(next);
        index++;
        continue;
      }

      out += escaped(0xfffd);
      continue;
    }

    if (code >= 0xdc00 &&
        code <= 0xdfff) {
      out += escaped(0xfffd);
      continue;
    }

    out += escaped(code);
  }

  return `${out}"`;
}

// An object, its members in the order the literal names them. Every member of the wire type has to be
// named, the optional ones too, so a member the vocabulary gains is a compile error here rather than
// a member this client never sends. One left undefined is omitted, as a null one is on the server.
type Members<T> = { readonly [K in keyof Required<T>]: string | undefined };

function object<T>(members: Members<T>): string {
  const written: string[] = [];
  for (const [name, json] of Object.entries(members) as [string, string | undefined][]) {
    if (json !== undefined) {
      written.push(`${quote(name)}:${json}`);
    }
  }

  return `{${written.join(",")}}`;
}

function array<T>(items: readonly T[], write: (item: T) => string): string {
  return `[${items.map(write).join(",")}]`;
}

function optional<T>(value: T | undefined, write: (value: T) => string): string | undefined {
  if (value === undefined) {
    return undefined;
  }

  return write(value);
}

function integer(value: number): string {
  if (!Number.isSafeInteger(value)) {
    throw new Error(`${value} is not a whole number, which is all the wire carries here.`);
  }

  return String(value);
}

function boolean(value: boolean): string {
  if (value) {
    return "true";
  }

  return "false";
}

// One segment as a bare string, any other number as an array: each path has one spelling.
function path(value: Path): string {
  const segments = typeof value === "string" ? [value] : value;
  if (segments.length === 1) {
    return quote(segments[0]!);
  }

  return array(segments, quote);
}

export function serializeRequest(request: QueryRequest): string {
  return object<QueryRequest>({
    version: integer(request.version),
    root: quote(request.root),
    pipeline: array(request.pipeline, operator),
    stamp: optional(request.stamp, quote)
  });
}

export function serializeBatch(batch: QueryBatchRequest): string {
  return object<QueryBatchRequest>({
    version: integer(batch.version),
    queries: array(batch.queries, serializeRequest)
  });
}

/** A request for one attachment's bytes, by the source, the member and the row's key. */
export function serializeAttachment(request: AttachmentRequest): string {
  return object<AttachmentRequest>({
    version: integer(request.version),
    root: quote(request.root),
    member: quote(request.member),
    keys: array(request.keys, key => object<AttachmentKey>({ value: optional(key.value, quote), tag: quote(key.tag) })),
    stamp: optional(request.stamp, quote)
  });
}

/**
 * A command's request, around its payload already written: a payload's members are the model's, which
 * the wire vocabulary does not name.
 */
export function serializeCommand(request: Omit<CommandRequest, "payload">, payload: string): string {
  return object<CommandRequest>({
    version: integer(request.version),
    command: quote(request.command),
    id: quote(request.id),
    payload,
    stamp: optional(request.stamp, quote)
  });
}

function operator(op: QueryOp): string {
  switch (op.$type) {
    case "where":
      return object<typeof op>({ $type: quote(op.$type), predicate: node(op.predicate) });
    case "orderBy":
    case "thenBy":
      return object<typeof op>({ $type: quote(op.$type), key: node(op.key), descending: boolean(op.descending) });
    case "skip":
    case "take":
      return object<typeof op>({ $type: quote(op.$type), count: integer(op.count) });
    case "select":
      return object<typeof op>({ $type: quote(op.$type), projection: projection(op.projection) });
    case "selectMany":
      return object<typeof op>({ $type: quote(op.$type), path: path(op.path) });
    case "ofType":
      return object<typeof op>({ $type: quote(op.$type), type: quote(op.type) });
    case "groupBy":
      return object<typeof op>({ $type: quote(op.$type), keys: array(op.keys, node) });
    case "distinct":
    case "reverse":
      return object<typeof op>({ $type: quote(op.$type) });
    case "join":
      return object<typeof op>({
        $type: quote(op.$type),
        root: quote(op.root),
        kind: quote(op.kind),
        outerKey: node(op.outerKey),
        innerKey: node(op.innerKey),
        innerPredicate: optional(op.innerPredicate, node),
        result: array(op.result, joinMember),
        innerOps: optional(op.innerOps, _ => array(_, operator))
      });
    case "set":
      return object<typeof op>({
        $type: quote(op.$type),
        kind: quote(op.kind),
        root: quote(op.root),
        predicate: optional(op.predicate, node),
        projection: projection(op.projection),
        operandOps: optional(op.operandOps, _ => array(_, operator))
      });
    case "count":
    case "longCount":
    case "any":
      return object<typeof op>({ $type: quote(op.$type), predicate: optional(op.predicate, node) });
    case "all":
      return object<typeof op>({ $type: quote(op.$type), predicate: node(op.predicate) });
    case "first":
    case "single":
    case "last":
      return object<typeof op>({
        $type: quote(op.$type),
        orDefault: boolean(op.orDefault),
        predicate: optional(op.predicate, node)
      });
    case "aggregate":
      return object<typeof op>({ $type: quote(op.$type), function: quote(op.function), selector: node(op.selector) });
    case "page":
      return object<typeof op>({
        $type: quote(op.$type),
        size: optional(op.size, integer),
        cursor: optional(op.cursor, quote)
      });
  }
}

function node(value: Node): string {
  switch (value.$type) {
    case "member":
      return object<typeof value>({ $type: quote(value.$type), path: path(value.path) });
    case "element":
      return object<typeof value>({ $type: quote(value.$type) });
    case "const":
      return object<typeof value>({ $type: quote(value.$type), value: optional(value.value, quote), tag: quote(value.tag) });
    case "binary":
      return object<typeof value>({
        $type: quote(value.$type),
        op: quote(value.op),
        left: node(value.left),
        right: node(value.right)
      });
    case "unary":
      return object<typeof value>({ $type: quote(value.$type), op: quote(value.op), operand: node(value.operand) });
    case "call":
      return object<typeof value>({
        $type: quote(value.$type),
        function: quote(value.function),
        target: node(value.target),
        arguments: array(value.arguments, node)
      });
    case "conditional":
      return object<typeof value>({
        $type: quote(value.$type),
        test: node(value.test),
        ifTrue: node(value.ifTrue),
        ifFalse: node(value.ifFalse)
      });
    case "subquery":
      return object<typeof value>({
        $type: quote(value.$type),
        path: path(value.path),
        function: quote(value.function),
        predicate: optional(value.predicate, node),
        selector: optional(value.selector, node)
      });
    case "collate":
      return object<typeof value>({ $type: quote(value.$type), target: node(value.target), match: quote(value.match) });
    case "inSource":
      return object<typeof value>({
        $type: quote(value.$type),
        value: node(value.value),
        root: quote(value.root),
        selector: node(value.selector),
        predicate: optional(value.predicate, node)
      });
    case "aggregate":
      return object<typeof value>({ $type: quote(value.$type), ...aggregate(value) });
    case "groupKey":
      return object<typeof value>({ $type: quote(value.$type), index: integer(value.index) });
    case "compositeKey":
      return object<typeof value>({ $type: quote(value.$type), parts: array(value.parts, node) });
  }
}

// An aggregate's members, which a join's result also carries without the discriminator.
function aggregate(value: Omit<AggregateNode, "$type">): Members<Omit<AggregateNode, "$type">> {
  let distinct: string | undefined;
  if (value.distinct === true) {
    distinct = "true";
  }

  return {
    function: quote(value.function),
    selector: optional(value.selector, node),
    separator: optional(value.separator, quote),
    predicate: optional(value.predicate, node),
    distinct
  };
}

function joinMember(member: JoinMember): string {
  return object<JoinMember>({
    name: quote(member.name),
    side: quote(member.side),
    path: path(member.path),
    aggregate: optional(member.aggregate, _ => object<Omit<AggregateNode, "$type">>(aggregate(_)))
  });
}

function projection(value: Projection): string {
  return object<Projection>({ members: array(value.members, entry) });
}

// A member reading the member it is named for travels as its bare name, and every other as an object:
// one spelling for each.
function entry(value: ProjectionEntry): string {
  if (typeof value === "string") {
    return quote(value);
  }

  if (readsItsOwnName(value.name, value.value)) {
    return quote(value.name);
  }

  return object<typeof value>({ name: quote(value.name), value: projectionValue(value.value) });
}

function readsItsOwnName(name: string, value: ProjectionValue): boolean {
  if (value.$type !== "node" ||
      value.node.$type !== "member") {
    return false;
  }

  const segments = typeof value.node.path === "string" ? [value.node.path] : value.node.path;
  return segments.length === 1 && segments[0] === name;
}

function projectionValue(value: ProjectionValue): string {
  switch (value.$type) {
    case "node":
      return object<typeof value>({ $type: quote(value.$type), node: node(value.node) });
    case "nested":
      return object<typeof value>({ $type: quote(value.$type), path: path(value.path), projection: projection(value.projection) });
    case "collection":
      return object<typeof value>({
        $type: quote(value.$type),
        path: path(value.path),
        predicate: optional(value.predicate, node),
        selector: optional(value.selector, node),
        projection: optional(value.projection, projection)
      });
  }
}
