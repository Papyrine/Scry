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

/** A response this client cannot read: cut short, not the wire's JSON, or not the answer asked for. */
export class ScryWireError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "ScryWireError";
  }
}
