// A live query: one request, answered whenever its answer changes, for as long as it is consumed. A
// connection that ends is asked for again, naming the last answer held, so a consumer sees no more
// than a pause. What ends it for good is what asking again would not fix: a rejection, a denial, a
// client the server calls stale. The same rules as the C# client's LivePump.

import { ScryRequestError, ScryStaleClientError, ScryWireError, refused, statusFor } from "../errors.ts";
import { parseJson } from "../response/json.ts";
import { isObject, refusalIn } from "../response/refusal.ts";
import type { LiveEvent } from "./query.ts";

/** What a retry policy is told: how many times in a row asking has failed, and why the last did. */
export interface RetryContext {
  /** Attempts since an answer last arrived. */
  readonly retries: number;
  /** Milliseconds since an answer last arrived. */
  readonly quietFor: number;
  /** What ended the last connection, where it failed; nothing where the server ended it. */
  readonly failure: unknown;
}

/** How long to wait before asking again, in milliseconds, or undefined to stop. */
export type RetryPolicy = (context: RetryContext) => number | undefined;

/** At once, then after a second, doubling to thirty, scattered a fifth either way, for as long as it takes. */
export const backoff: RetryPolicy = context => {
  if (context.retries === 0) {
    return 0;
  }

  const seconds = Math.min(30, 2 ** Math.min(context.retries - 1, 5));
  return seconds * 1000 * (0.8 + Math.random() * 0.4);
};

/** Opens one connection, naming the last answer held, until the signal aborts. */
export type Opener = (lastEventId: string | undefined, signal: AbortSignal) => AsyncIterable<LiveEvent>;

/** Each answer, read from its event's data. */
export type Reader<T> = (data: string) => T;

export class LiveQuery<T> implements AsyncIterable<T> {
  readonly #open: Opener;
  readonly #read: Reader<T>;
  readonly #retry: RetryPolicy;
  readonly #signal: AbortSignal | undefined;

  /** `signal` ends it for good, every connection it holds closed. */
  constructor(open: Opener, read: Reader<T>, retry: RetryPolicy, signal?: AbortSignal) {
    this.#open = open;
    this.#read = read;
    this.#retry = retry;
    this.#signal = signal;
  }

  /**
   * Every answer, until the loop leaves or the live query's signal aborts, which ends the loop with the
   * signal's reason, whether it was waiting for an answer or between two connections.
   */
  [Symbol.asyncIterator](): AsyncIterator<T> {
    return this.#until(this.#signal ?? new AbortController().signal);
  }

  async *#until(signal: AbortSignal): AsyncGenerator<T> {
    yield* this.#answers(signal);
    // The answers end quietly when the signal aborts, which is how a subscription stops, but a loop
    // that stopped at the signal did not reach the end of anything.
    signal.throwIfAborted();
  }

  /**
   * Calls `onAnswer` with every answer until the returned function is called or the live query's
   * signal aborts, or until the live query ends for good, which `onError` is told.
   */
  subscribe(onAnswer: (answer: T) => void, onError?: (error: unknown) => void): () => void {
    const stop = new AbortController();
    const signal = this.#signal;
    const forward = () => stop.abort();
    if (signal?.aborted === true) {
      stop.abort();
    } else {
      signal?.addEventListener("abort", forward, { once: true });
    }

    void (async () => {
      try {
        for await (const answer of this.#answers(stop.signal)) {
          onAnswer(answer);
        }
      } catch (error) {
        if (!stop.signal.aborted) {
          onError?.(error);
        }
      } finally {
        signal?.removeEventListener("abort", forward);
      }
    })();
    return () => stop.abort();
  }

  async *#answers(signal: AbortSignal): AsyncGenerator<T> {
    let lastId: string | undefined;
    let lastData: string | undefined;
    let retries = 0;
    let quietSince = Date.now();
    while (!signal.aborted) {
      let failure: unknown;
      let reconnect = true;
      let first = true;
      const connection = new AbortController();
      const end = () => connection.abort();
      signal.addEventListener("abort", end, { once: true });
      try {
        for await (const event of this.#open(lastId, connection.signal)) {
          if (event.event === "end") {
            reconnect = ended(event.data);
            break;
          }

          if (event.event === "error") {
            throw refusedBy(event.data);
          }

          if (event.event !== "result" &&
              event.event !== "unchanged") {
            // A heartbeat, or an event from a server newer than this client: neither is an answer.
            continue;
          }

          retries = 0;
          quietSince = Date.now();
          // A transport that names no answer sends the one already held each time it is asked again.
          const repeated = first &&
                           event.id === undefined &&
                           event.data === lastData;
          first = false;
          if (event.event === "unchanged" ||
              repeated) {
            continue;
          }

          lastId = event.id;
          lastData = event.data;
          yield this.#read(event.data);
        }
      } catch (error) {
        if (signal.aborted) {
          return;
        }

        if (!worthAskingAgain(error)) {
          throw error;
        }

        failure = error;
      } finally {
        signal.removeEventListener("abort", end);
        connection.abort();
      }

      if (signal.aborted ||
          !reconnect) {
        return;
      }

      const delay = this.#retry({ retries, quietFor: Date.now() - quietSince, failure });
      if (delay === undefined) {
        throw failure ?? new ScryWireError("The live query's connection ended, and the retry policy declined to ask again.");
      }

      retries++;
      if (delay > 0) {
        await wait(delay, signal);
      }
    }
  }
}

function ended(data: string): boolean {
  const parsed = parseJson(data);
  if (isObject(parsed) &&
      parsed["reconnect"] === false) {
    return false;
  }

  return true;
}

// An error event says why the server closed the stream, carrying the code its status would have.
function refusedBy(data: string): Error {
  let refusal = undefined;
  try {
    refusal = refusalIn(parseJson(data));
  } catch {
    // Read as a failure with no code below.
  }

  if (refusal === undefined) {
    return new ScryRequestError(500, "Unknown", `The server ended the live query: ${data}`, false, undefined);
  }

  if (refusal.code === "StaleClient") {
    return new ScryStaleClientError(refusal.error);
  }

  return refused(statusFor(refusal.code), refusal, undefined);
}

// What a later attempt could get past: the server busy or failing for the moment, something in the way
// answering as it does while the server behind it restarts, or the connection itself failing.
function worthAskingAgain(error: unknown): boolean {
  if (error instanceof ScryRequestError) {
    if (error.code === "ExecutionFailed" ||
        error.code === "SubscriptionLimit") {
      return true;
    }

    return error.code === "Unknown" &&
           (error.status >= 500 || error.status === 408 || error.status === 429);
  }

  // fetch's own failures: the connection could not be made, or was cut.
  return error instanceof TypeError ||
         (error instanceof DOMException && error.name !== "AbortError");
}

/** @internal Resolves after the delay, or as soon as the signal aborts. */
export function wait(milliseconds: number, signal: AbortSignal): Promise<void> {
  return new Promise(resolve => {
    const timer = setTimeout(done, milliseconds);
    signal.addEventListener("abort", done, { once: true });

    function done() {
      clearTimeout(timer);
      signal.removeEventListener("abort", done);
      resolve();
    }
  });
}
