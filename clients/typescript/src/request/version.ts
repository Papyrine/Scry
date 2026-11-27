// The lowest wire version that carries a pipeline whole, as WireFormat.RequiredVersion decides it: a
// request using only what version 1 named stays version 1, so it keeps working against a server that
// predates version 2. Only new fields on existing shapes are named here; a new kind of node fails an
// older server by its discriminator alone.

import type { Node, Projection, QueryOp } from "../wire.ts";

export function requiredVersion(pipeline: readonly QueryOp[]): number {
  for (const op of pipeline) {
    if (richerOp(op)) {
      return 2;
    }
  }

  return 1;
}

function richerOp(op: QueryOp): boolean {
  switch (op.$type) {
    case "join":
      return op.innerOps !== undefined ||
             richer(op.outerKey) ||
             richer(op.innerKey) ||
             richer(op.innerPredicate) ||
             op.result.some(_ => _.aggregate !== undefined && richer({ $type: "aggregate", ..._.aggregate }));
    case "set":
      return op.operandOps !== undefined ||
             richer(op.predicate) ||
             richerProjection(op.projection);
    case "where":
      return richer(op.predicate);
    case "orderBy":
    case "thenBy":
      return richer(op.key);
    case "select":
      return richerProjection(op.projection);
    case "groupBy":
      return op.keys.some(richer);
    case "aggregate":
      return richer(op.selector);
    case "count":
    case "longCount":
    case "any":
    case "first":
    case "single":
    case "last":
      return richer(op.predicate);
    case "all":
      return richer(op.predicate);
    case "skip":
    case "take":
    case "selectMany":
    case "ofType":
    case "distinct":
    case "reverse":
    case "page":
      return false;
  }
}

function richer(node: Node | undefined): boolean {
  if (node === undefined) {
    return false;
  }

  switch (node.$type) {
    case "aggregate":
      return node.predicate !== undefined ||
             node.distinct === true ||
             richer(node.selector);
    case "binary":
      return richer(node.left) || richer(node.right);
    case "unary":
      return richer(node.operand);
    case "conditional":
      return richer(node.test) || richer(node.ifTrue) || richer(node.ifFalse);
    case "call":
      return richer(node.target) || node.arguments.some(richer);
    case "collate":
      return richer(node.target);
    case "subquery":
      return richer(node.predicate) || richer(node.selector);
    case "inSource":
      return richer(node.value) || richer(node.selector) || richer(node.predicate);
    case "compositeKey":
      return node.parts.some(richer);
    case "member":
    case "element":
    case "const":
    case "groupKey":
      return false;
  }
}

function richerProjection(projection: Projection | undefined): boolean {
  if (projection === undefined) {
    return false;
  }

  return projection.members.some(_ => {
    if (typeof _ === "string") {
      return false;
    }

    switch (_.value.$type) {
      case "node":
        return richer(_.value.node);
      case "nested":
        return richerProjection(_.value.projection);
      case "collection":
        return richer(_.value.predicate) || richer(_.value.selector) || richerProjection(_.value.projection);
    }
  });
}
