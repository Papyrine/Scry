// The client: a description's sources as typed queries, the operators and functions a query is written
// with, and the request each sends.

export * from "./runtime/index.ts";
export { createScry, schemaStatus } from "./query/query.ts";
export type { Answer, Final, Grouped, Ordered, Page, Projected, RequestContext, Rows, Scry, ScryOptions, Transport } from "./query/query.ts";
export { httpTransport } from "./http.ts";
export type { HttpOptions } from "./http.ts";
export { ScryPermissionError, ScryRequestError, ScryStaleClientError, ScryWireError } from "./errors.ts";
export {
  add,
  and,
  coalesce,
  concat,
  cond,
  div,
  eq,
  ge,
  gt,
  hasValue,
  ignoringCase,
  isIn,
  isNull,
  le,
  lt,
  matchingCase,
  mod,
  mul,
  ne,
  neg,
  not,
  or,
  sub
} from "./query/operators.ts";
export type { Operand, Plain } from "./query/operators.ts";
export * from "./query/functions.ts";
export { serializeRequest, serializeBatch } from "./request/json.ts";
export { requiredVersion } from "./request/version.ts";
export type * from "./wire.ts";
export { base64Url, encodeQuery, formFor } from "./request/url.ts";
export type { Form } from "./request/url.ts";
