// Several queries collected on the client and sent as one request, each answered as it would have
// been alone: a refusal of one is that entry's failure, and the others are answered. A batch is used
// once, as ScryBatch is.

import { WireFormat, type QueryRequest } from "../wire.ts";
import { refused, ScryStaleClientError, ScryWireError, statusFor } from "../errors.ts";
import { serializeBatch } from "../request/json.ts";
import { parseJson, type Json } from "../response/json.ts";
import { isObject, refusalIn } from "../response/refusal.ts";
import { rebind, type Answer, type RequestContext, type Scry, type Transport } from "./query.ts";

interface Entry {
  readonly request: QueryRequest;
  resolve(answer: Answer): void;
  reject(error: unknown): void;
}

// The statuses a batch entry names, by HttpStatusCode's names.
const statuses: Readonly<Record<string, number>> = {
  BadRequest: 400,
  Unauthorized: 401,
  Forbidden: 403,
  NotFound: 404,
  RequestEntityTooLarge: 413,
  UnsupportedMediaType: 415,
  TooManyRequests: 429,
  InternalServerError: 500,
  ServiceUnavailable: 503
};

export class Batch<S> {
  readonly #scry: Scry<S>;
  readonly #entries: Entry[] = [];
  #transport: Transport | undefined;
  #sent = false;
  #refusal: Error | undefined;

  constructor(scry: Scry<S>) {
    this.#scry = rebind(scry, original => {
      this.#transport = original;
      return { query: (request, context) => this.#collect(request, context) };
    });
  }

  /**
   * Adds a query to the batch: `run` ends one query with its terminal, on the sources it is given. The
   * answer arrives once the batch is sent.
   */
  add<T>(run: (scry: Scry<S>) => Promise<T>): Promise<T> {
    if (this.#sent) {
      throw new Error("This batch has been sent: start another for more queries.");
    }

    const before = this.#entries.length;
    const answer = run(this.#scry);
    const refusal = this.#refusal;
    if (refusal !== undefined) {
      this.#refusal = undefined;
      answer.catch(() => {});
      throw refusal;
    }

    if (this.#entries.length !== before + 1) {
      throw new Error("A batch entry is one query ended by its terminal: toList(), count(), first() and the rest.");
    }

    // Read whenever the caller awaits it; marked handled here so a refused entry awaited after send()
    // is not reported as unhandled in between.
    answer.catch(() => {});
    return answer;
  }

  /** Sends every query added, as one request. */
  async send(signal?: AbortSignal): Promise<void> {
    if (this.#sent) {
      throw new Error("This batch has been sent already.");
    }

    this.#sent = true;
    const entries = this.#entries;
    if (entries.length === 0) {
      return;
    }

    const transport = this.#transport;
    if (transport?.batch === undefined) {
      const error = new Error("This client's transport does not batch: httpTransport() does.");
      entries.forEach(_ => _.reject(error));
      throw error;
    }

    let answer: Answer;
    try {
      answer = await transport.batch(serializeBatch({ version: WireFormat.Version, queries: entries.map(_ => _.request) }), signal);
    } catch (error) {
      entries.forEach(_ => _.reject(error));
      throw error;
    }

    let results: Json[];
    try {
      const envelope = answer.json ?? parseJson(answer.text ?? "");
      if (!isObject(envelope) ||
          !Array.isArray(envelope["results"]) ||
          envelope["results"].length !== entries.length) {
        throw new ScryWireError(`The batch's answer does not hold one result for each of its ${entries.length} entries.`);
      }

      results = envelope["results"];
    } catch (error) {
      entries.forEach(_ => _.reject(error));
      throw error;
    }

    results.forEach((result, index) => settle(entries[index]!, result, answer.stamp, answer.parts));
  }

  // A batch is one request, so a query's own headers have no request of their own to go on: refused
  // rather than dropped, as ScryBatch refuses them.
  #collect(request: QueryRequest, context: RequestContext): Promise<Answer> {
    if (context.headers !== undefined ||
        context.onResponseHeaders !== undefined) {
      this.#refusal = new Error("A query in a batch cannot carry headers of its own: the batch is one request. Send the query alone, or give the transport the header.");
      return Promise.reject(this.#refusal);
    }

    return new Promise((resolve, reject) => this.#entries.push({ request, resolve, reject }));
  }
}

// A batch's parts are numbered across every entry, so each entry's placeholders index the same list.
function settle(entry: Entry, result: Json, stamp: string | undefined, parts: readonly Uint8Array[] | undefined): void {
  if (isObject(result) &&
      isObject(result["response"])) {
    entry.resolve({ json: result["response"], ...(stamp === undefined ? {} : { stamp }), ...(parts === undefined ? {} : { parts }) });
    return;
  }

  const refusal = refusalIn(result);
  if (refusal === undefined) {
    entry.reject(new ScryWireError("A batch entry was answered with neither a response nor an error."));
    return;
  }

  if (refusal.code === "StaleClient") {
    entry.reject(new ScryStaleClientError(refusal.error));
    return;
  }

  let status = statusFor(refusal.code);
  if (isObject(result) &&
      typeof result["status"] === "string" &&
      statuses[result["status"]] !== undefined) {
    status = statuses[result["status"]]!;
  }

  entry.reject(refused(status, refusal, stamp));
}

/** A batch over the sources of `scry`: add queries to it, then send them as one request. */
export function createBatch<S>(scry: Scry<S>): Batch<S> {
  return new Batch(scry);
}
