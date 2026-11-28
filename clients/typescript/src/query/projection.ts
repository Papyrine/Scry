// What a query returns, from the object a `select` function builds: each key a member of the row, and
// each value what it reads. The same reading the C# translator makes of an anonymous type or a record,
// so the projection travels as the C# one does.

import type { CollectionValue, Node, Projection, ProjectionMember } from "../wire.ts";
import { camelCase } from "../runtime/naming.ts";
import { AttachmentReference, Collection, CollectionProjection, Expression, isRow, operand, type Scope } from "./expression.ts";
import { valuePlan, type AttachmentBinding, type Plan, type PlanMember } from "../response/decode.ts";

export interface Built {
  readonly projection: Projection;
  readonly sensitive: boolean;
  /** What the rows it returns are, to read them back by. */
  readonly plan: Plan;
  /** The attachments its rows carry, which travel as nothing and come back as handles. */
  readonly attachments: readonly AttachmentBinding[];
}

type Member = { value: ProjectionMember["value"]; sensitive: boolean; plan: Plan; attachments?: Pending[] };

// An attachment met in a projection, before its key has been looked for among the members beside it:
// whether that key was projected is a question about the whole projection.
interface Pending {
  readonly target: readonly string[];
  readonly reference: AttachmentReference;
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
  const planned: PlanMember[] = [];
  const pending: Pending[] = [];
  let sensitive = false;
  for (const [key, member] of entries(value)) {
    // Travels as nothing: no response carries an attachment.
    if (member instanceof AttachmentReference) {
      if (grouped) {
        throw new Error(`'${key}': an attachment cannot be returned out of a group, which folds many rows into one and leaves no row key to fetch it by.`);
      }

      pending.push({ target: [key], reference: member });
      continue;
    }

    const built = memberValue(member, grouped, scope, key);
    const wire = wireName(key);
    members.push({ name: wire, value: built.value });
    planned.push({ key, wire, plan: built.plan });
    pending.push(...built.attachments ?? []);
    sensitive ||= built.sensitive;
  }

  if (members.length === 0) {
    throw new Error("A projection needs at least one member.");
  }

  const projection = { members };
  const attachments = resolve(pending, projection, planned);
  let plan: Plan = { kind: "object", members: planned };
  if (attachments.length > 0) {
    plan = { ...plan, attachments };
  }

  return { projection, sensitive, plan, attachments };
}

// Each attachment matched to where its row's key landed: a key member projected as a member of its own,
// beside it. One left out is refused here, rather than producing a handle that fails when it is opened
// with nothing to say why.
function resolve(pending: readonly Pending[], projection: Projection, planned: readonly PlanMember[]): AttachmentBinding[] {
  if (pending.length === 0) {
    return [];
  }

  const leaves = new Map<string, readonly string[]>();
  collectLeaves(projection, [], planned, [], leaves);
  return pending.map(({ target, reference }) => ({
    target,
    root: reference.root,
    member: reference.member,
    keys: reference.keys.map(key => {
      const wanted = [...reference.prefix, key.member];
      const path = leaves.get(wanted.join("."));
      if (path === undefined) {
        throw new Error(`The attachment '${reference.name}' needs _.${wanted.map(camelCase).join(".")} returned beside it: an attachment is fetched by its row's key, so the key has to come back with the row.`);
      }

      return { path, value: key.value };
    })
  }));
}

// Every member path the projection reads as a plain member, and where in the row its value lands. A
// computed value is not a key, whatever it was computed from.
function collectLeaves(projection: Projection, prefix: readonly string[], planned: readonly PlanMember[], output: readonly string[], leaves: Map<string, readonly string[]>): void {
  projection.members.forEach((member, index) => {
    const key = planned[index]!.key;
    if (typeof member === "string") {
      leaves.set([...prefix, member].join("."), [...output, key]);
      return;
    }

    const value = member.value;

    if (value.$type === "node" &&
        value.node.$type === "member") {
      leaves.set([...prefix, ...segments(value.node.path)].join("."), [...output, key]);
      return;
    }

    const nested = planned[index]!.plan;
    if (value.$type === "nested" &&
        nested.kind === "object") {
      collectLeaves(value.projection, [...prefix, ...segments(value.path)], nested.members, [...output, key], leaves);
    }
  });
}

function memberValue(value: unknown, grouped: boolean, scope: Scope, key: string): Member {
  if (value instanceof Expression) {
    return { value: { $type: "node", node: value.node }, sensitive: value.sensitive, plan: valuePlan(value.value) };
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
  return { value: { $type: "node", node: constant.node }, sensitive: false, plan: valuePlan(undefined) };
}

// An object inside the row reads one navigation, which the member paths it reads all share: they are
// rebased onto it, and the object travels as a nested projection into it.
function nested(value: Record<string, unknown>, key: string): Member {
  const members: [string, Expression][] = [];
  const attachments: Pending[] = [];
  for (const [name, member] of entries(value)) {
    if (member instanceof AttachmentReference) {
      attachments.push({ target: [key, name], reference: member });
      continue;
    }

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

  for (const { reference } of attachments) {
    paths.push([...reference.prefix, reference.member]);
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
    sensitive: members.some(([, member]) => member.sensitive),
    plan: { kind: "object", members: members.map(([name, member]) => ({ key: name, wire: wireName(name), plan: valuePlan(member.value) })) },
    attachments
  };
}

function collection(value: Collection | CollectionProjection, key: string): Member {
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
    return { value: { ...result, selector: correlated(selected.node, source.path) }, sensitive, plan: { kind: "array", element: valuePlan(selected.value) } };
  }

  if (!plain(selected)) {
    throw new Error(`What '${key}' returns of each element is a value, or a flat object of values.`);
  }

  const members: ProjectionMember[] = [];
  const planned: PlanMember[] = [];
  for (const [name, member] of entries(selected)) {
    if (!(member instanceof Expression)) {
      throw new Error(`'${key}.${name}': an element of a projected collection is a value or a flat object of values.`);
    }

    members.push({ name: wireName(name), value: { $type: "node", node: correlated(member.node, source.path) } });
    planned.push({ key: name, wire: wireName(name), plan: valuePlan(member.value) });
    sensitive ||= member.sensitive;
  }

  if (members.length === 0) {
    throw new Error(`An element of '${key}' needs at least one member.`);
  }

  return {
    value: { ...result, projection: { members } },
    sensitive,
    plan: { kind: "array", element: { kind: "object", members: planned } }
  };
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
