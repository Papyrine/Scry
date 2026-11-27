// What a query returns, from the object a `select` function builds: each key a member of the row, and
// each value what it reads. The same reading the C# translator makes of an anonymous type or a record,
// so the projection travels as the C# one does.

import type { CollectionValue, Node, Projection, ProjectionMember } from "../wire.ts";
import { camelCase } from "../runtime/naming.ts";
import { Collection, CollectionProjection, Expression, isRow, operand, type Scope } from "./expression.ts";

export interface Built {
  readonly projection: Projection;
  readonly sensitive: boolean;
}

/**
 * The name a key travels under: its first letter a capital, as a C# member's is, so a query sends the
 * bytes a C# one does. A row writes every name camel-cased, so the key reads back as it was written.
 * One the policy would not read back that way, or that starts with no letter, travels as it is.
 */
export function wireName(key: string): string {
  if (key.startsWith("$")) {
    throw new Error(`'${key}' starts with '$', which the wire keeps for its own markers.`);
  }

  if (key.length === 0) {
    throw new Error("A projected member needs a name.");
  }

  const first = key.charAt(0);
  const capital = first.toUpperCase();
  const written = capital + key.slice(1);
  if (capital.length === 1 &&
      capital !== first &&
      camelCase(written) === key) {
    return written;
  }

  return key;
}

function plain(value: unknown): value is Record<string, unknown> {
  if (typeof value !== "object" ||
      value === null ||
      isRow(value) ||
      value instanceof Expression ||
      value instanceof Collection ||
      value instanceof CollectionProjection ||
      Array.isArray(value) ||
      value instanceof Date ||
      value instanceof Uint8Array) {
    return false;
  }

  const prototype = Object.getPrototypeOf(value);
  return prototype === Object.prototype || prototype === null;
}

function entries(value: Record<string, unknown>): [string, unknown][] {
  const named = Object.entries(value);
  const written = new Set<string>();
  for (const [key] of named) {
    // Two names a row writes alike would be one key in every row; the server refuses them too.
    const cased = camelCase(wireName(key));
    if (written.has(cased)) {
      throw new Error(`'${key}' is projected twice: a row writes it as '${cased}', as it does another member.`);
    }

    written.add(cased);
  }

  return named;
}

/** A projection from the object a `select` function returned. Over a group, every member is a node. */
export function projectionOf(value: unknown, grouped: boolean, scope: Scope): Built {
  if (!plain(value)) {
    throw new Error("A projection is an object, each of its members a value the row returns.");
  }

  const members: ProjectionMember[] = [];
  let sensitive = false;
  for (const [key, member] of entries(value)) {
    const built = memberValue(member, grouped, scope, key);
    members.push({ name: wireName(key), value: built.value });
    sensitive ||= built.sensitive;
  }

  if (members.length === 0) {
    throw new Error("A projection needs at least one member.");
  }

  return { projection: { members }, sensitive };
}

function memberValue(value: unknown, grouped: boolean, scope: Scope, key: string): { value: ProjectionMember["value"]; sensitive: boolean } {
  if (value instanceof Expression) {
    return { value: { $type: "node", node: value.node }, sensitive: value.sensitive };
  }

  if (isRow(value)) {
    throw new Error(`'${key}' is a navigation, which is not a value: return its members, or an object of them.`);
  }

  if (value instanceof Collection ||
      value instanceof CollectionProjection) {
    if (grouped) {
      throw new Error(`'${key}': a grouped query returns the group's key and what it folds, not a collection.`);
    }

    return collection(value, key);
  }

  if (plain(value)) {
    if (grouped) {
      throw new Error(`'${key}': a grouped query returns a flat row.`);
    }

    return nested(value, key);
  }

  const constant = operand(value, undefined, "argument", scope);
  return { value: { $type: "node", node: constant.node }, sensitive: false };
}

// An object inside the row reads one navigation, which the member paths it reads all share: they are
// rebased onto it, and the object travels as a nested projection into it.
function nested(value: Record<string, unknown>, key: string): { value: ProjectionMember["value"]; sensitive: boolean } {
  const members: [string, Expression][] = [];
  for (const [name, member] of entries(value)) {
    if (!(member instanceof Expression)) {
      throw new Error(`'${key}.${name}' is not a value. A nested object holds values, one level deep.`);
    }

    members.push([name, member]);
  }

  if (members.length === 0) {
    throw new Error(`'${key}' needs at least one member.`);
  }

  const paths: (readonly string[])[] = [];
  for (const [, member] of members) {
    collectPaths(member.node, paths);
  }

  const prefix = commonNavigationPrefix(paths);
  if (prefix.length === 0) {
    throw new Error(`'${key}' has to read from one navigation, every member sharing it (each reading, say, _.department).`);
  }

  return {
    value: {
      $type: "nested",
      path: prefix,
      projection: {
        members: members.map(([name, member]) => ({ name: wireName(name), value: { $type: "node", node: stripPrefix(member.node, prefix.length) } }))
      }
    },
    sensitive: members.some(([, member]) => member.sensitive)
  };
}

function collection(value: Collection | CollectionProjection, key: string): { value: ProjectionMember["value"]; sensitive: boolean } {
  let source: Collection;
  let selected: unknown;
  if (value instanceof CollectionProjection) {
    source = value.collection;
    selected = value.selected;
  } else {
    source = value;
    if ("model" in source.element) {
      throw new Error(`'${key}' is a collection of rows, so returning it needs a select() saying what to return of each: a value, or an object of values.`);
    }

    selected = new Expression({ $type: "element" }, source.element, source.scope, source.sensitive);
  }

  const result: CollectionValue = {
    $type: "collection",
    path: [...source.path],
    ...(source.filter === undefined ? {} : { predicate: correlated(source.filter.node, source.path) })
  };
  let sensitive = source.sensitive || (source.filter?.sensitive ?? false);

  if (selected instanceof Expression) {
    sensitive ||= selected.sensitive;
    return { value: { ...result, selector: correlated(selected.node, source.path) }, sensitive };
  }

  if (!plain(selected)) {
    throw new Error(`What '${key}' returns of each element is a value, or a flat object of values.`);
  }

  const members: ProjectionMember[] = [];
  for (const [name, member] of entries(selected)) {
    if (!(member instanceof Expression)) {
      throw new Error(`'${key}.${name}': an element of a projected collection is a value or a flat object of values.`);
    }

    members.push({ name: wireName(name), value: { $type: "node", node: correlated(member.node, source.path) } });
    sensitive ||= member.sensitive;
  }

  if (members.length === 0) {
    throw new Error(`An element of '${key}' needs at least one member.`);
  }

  return { value: { ...result, projection: { members } }, sensitive };
}

// An element carries what it reads off itself: never a question asked of another collection or source.
function correlated(node: Node, path: readonly string[]): Node {
  if (contains(node, _ => _.$type === "subquery" || _.$type === "inSource")) {
    throw new Error(`An element of '${path.join(".")}' may only carry values read off the element, not a question asked of another collection or source.`);
  }

  return node;
}

function contains(node: Node, test: (node: Node) => boolean): boolean {
  if (test(node)) {
    return true;
  }

  switch (node.$type) {
    case "binary":
      return contains(node.left, test) || contains(node.right, test);
    case "unary":
      return contains(node.operand, test);
    case "conditional":
      return contains(node.test, test) || contains(node.ifTrue, test) || contains(node.ifFalse, test);
    case "call":
      return contains(node.target, test) || node.arguments.some(_ => contains(_, test));
    case "collate":
      return contains(node.target, test);
    default:
      return false;
  }
}

function segments(path: string | readonly string[]): readonly string[] {
  if (typeof path === "string") {
    return [path];
  }

  return path;
}

function collectPaths(node: Node, paths: (readonly string[])[]): void {
  switch (node.$type) {
    case "member":
      paths.push(segments(node.path));
      break;
    case "subquery":
      paths.push(segments(node.path));
      break;
    case "inSource":
      collectPaths(node.value, paths);
      break;
    case "binary":
      collectPaths(node.left, paths);
      collectPaths(node.right, paths);
      break;
    case "unary":
      collectPaths(node.operand, paths);
      break;
    case "collate":
      collectPaths(node.target, paths);
      break;
    case "conditional":
      collectPaths(node.test, paths);
      collectPaths(node.ifTrue, paths);
      collectPaths(node.ifFalse, paths);
      break;
    case "call":
      collectPaths(node.target, paths);
      for (const argument of node.arguments) {
        collectPaths(argument, paths);
      }

      break;
    default:
      break;
  }
}

// The leading segments every path shares, stopping before any path's last, so each keeps one.
function commonNavigationPrefix(paths: readonly (readonly string[])[]): string[] {
  const prefix: string[] = [];
  if (paths.length === 0) {
    return prefix;
  }

  while (paths.every(_ => _.length > prefix.length + 1)) {
    const segment = paths[0]![prefix.length]!;
    if (paths.some(_ => _[prefix.length] !== segment)) {
      break;
    }

    prefix.push(segment);
  }

  return prefix;
}

function stripPrefix(node: Node, prefix: number): Node {
  switch (node.$type) {
    case "member":
      return { $type: "member", path: segments(node.path).slice(prefix) };
    case "subquery":
      return { ...node, path: segments(node.path).slice(prefix) };
    case "inSource":
      return { ...node, value: stripPrefix(node.value, prefix) };
    case "binary":
      return { ...node, left: stripPrefix(node.left, prefix), right: stripPrefix(node.right, prefix) };
    case "unary":
      return { ...node, operand: stripPrefix(node.operand, prefix) };
    case "collate":
      return { ...node, target: stripPrefix(node.target, prefix) };
    case "conditional":
      return {
        ...node,
        test: stripPrefix(node.test, prefix),
        ifTrue: stripPrefix(node.ifTrue, prefix),
        ifFalse: stripPrefix(node.ifFalse, prefix)
      };
    case "call":
      return { ...node, target: stripPrefix(node.target, prefix), arguments: node.arguments.map(_ => stripPrefix(_, prefix)) };
    default:
      return node;
  }
}
