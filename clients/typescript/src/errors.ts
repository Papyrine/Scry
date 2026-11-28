// What a query fails with. The server's refusals carry the code it answered with, which is what a
// caller branches on; a response this client cannot read is a failure of the wire, never a guess.

import type { ScryErrorCode } from "./wire.ts";

/** The server refused or failed a request: its status, its code, and the message it gave. */
export class ScryRequestError extends Error {
  readonly status: number;
  readonly code: ScryErrorCode;
  /** The request was refused for travelling as a URL, and would be answered as a body. */
  readonly requiresBody: boolean;
  /** The server's schema stamp, from the refusal's header. */
  readonly serverStamp: string | undefined;

  constructor(status: number, code: ScryErrorCode, message: string, requiresBody: boolean, serverStamp: string | undefined) {
    super(message);
    this.name = "ScryRequestError";
    this.status = status;
    this.code = code;
    this.requiresBody = requiresBody;
    this.serverStamp = serverStamp;
  }
}

/** The server's policy denied the request. */
export class ScryPermissionError extends ScryRequestError {
  constructor(status: number, message: string, serverStamp: string | undefined) {
    super(status, "Forbidden", message, false, serverStamp);
    this.name = "ScryPermissionError";
  }
}

/**
 * The client was generated against another model than the server's: refused by the server for it, or
 * answered with a value this client's model has no name for. Generate the client again.
 */
export class ScryStaleClientError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ScryStaleClientError";
  }
}

/** What the server refused with: its message, its code, and whether a body would be answered. */
export interface Refusal {
  readonly error: string;
  readonly code?: ScryErrorCode;
  readonly requiresBody?: boolean;
}

/**
 * The status an answer with no status line of its own (a stream's error marker, a batch's entry)
 * would have carried, as ResponseFailure.Status decides it on the .NET side.
 */
export function statusFor(code: ScryErrorCode | undefined): number {
  switch (code) {
    case "WireFormat":
    case "Validation":
    case "StaleClient":
      return 400;
    case "Forbidden":
      return 403;
    case "UnsupportedMedia":
      return 415;
    case "SubscriptionLimit":
    case "CommandLimit":
      return 503;
    case "NotFound":
      return 404;
    case "PayloadTooLarge":
      return 413;
    default:
      return 500;
  }
}

/** The error a refusal surfaces as, however it arrived. */
export function refused(status: number, refusal: Refusal, stamp: string | undefined): Error {
  if (refusal.code === "Forbidden") {
    return new ScryPermissionError(status, refusal.error, stamp);
  }

  return new ScryRequestError(status, refusal.code ?? "Unknown", refusal.error, refusal.requiresBody === true, stamp);
}

/**
 * The server maps no route for what was asked: it is not serving live queries, or commands, which are
 * off until a server says how many it will hold.
 */
export class ScryNotServedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ScryNotServedError";
  }
}

/** A response this client cannot read: cut short, not the wire's JSON, or not the answer asked for. */
export class ScryWireError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ScryWireError";
  }
}
