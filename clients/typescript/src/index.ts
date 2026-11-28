// The client: a description's sources as typed queries, the operators and functions a query is written
// with, and the request each sends.

export * from "./runtime/index.ts";
export { createScry, schemaStatus } from "./query/query.ts";
export type {
  Answer,
  Final,
  Grouped,
  LiveContext,
  LiveEvent,
  Ordered,
  Page,
  Projected,
  QueryOptions,
  RequestContext,
  Rows,
  Scry,
  ScryOptions,
  Transport
} from "./query/query.ts";
export { httpTransport } from "./http.ts";
export { createBatch, Batch } from "./query/batch.ts";
export { backoff, LiveQuery } from "./query/live.ts";
export type { RetryContext, RetryPolicy } from "./query/live.ts";
export type { HttpOptions } from "./http.ts";
export { createCommands, resultOf, ScryCommandFailedError, ScryCommands } from "./commands/commands.ts";
export type {
  CommandOutcome,
  CommandOutcomeStatus,
  CommandsOptions,
  CompletedOutcome,
  FailedOutcome,
  FinalOutcome,
  PendingCommand,
  PayloadInput,
  PendingOutcome,
  SendOptions,
  Senders,
  UnknownOutcome
} from "./commands/commands.ts";
export { ScryNotServedError, ScryPermissionError, ScryRequestError, ScryStaleClientError, ScryWireError } from "./errors.ts";
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
