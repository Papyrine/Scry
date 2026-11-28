// Commands: writes, sent as messages the server binds into its own classes, decides by its own
// policies and hands to a handler. A command the server decides at once answers with its outcome; one
// it does not answers pending, streams its outcome on the same response, and is followed here until
// it lands: asked for again by its id when that response ends first, and listed as pending work for a
// screen to show once the sender has stopped waiting. A refusal is not an outcome: a command the
// server would not accept never ran, so sending it throws, as a query the server refuses does. The
// same rules as the C# client's ScryClient.Commands.

import { CommandRequest, CommandStatus as ReceiptStatus } from "../wire.ts";
import type { CommandInfo } from "../runtime/schema.ts";
import type * as scry from "../runtime/types.ts";
import { camelCase } from "../runtime/naming.ts";
import { ScryNotServedError, ScryPermissionError, ScryRequestError, ScryWireError } from "../errors.ts";
import { serializeCommand } from "../request/json.ts";
import { parseJson, type Json } from "../response/json.ts";
import { Decoder, valuePlan, type Plan, type PlanMember } from "../response/decode.ts";
import { isObject } from "../response/refusal.ts";
import { backoff, wait, type RetryPolicy } from "../query/live.ts";
import { clientOf, type Answer, type ClientState, type Transport } from "../query/query.ts";
import type { Plain } from "../query/operators.ts";
import { writePayload } from "./payload.ts";

/**
 * Where a command is: decided, still being handled, or lost track of. The wire's CommandStatus has no
 * unknown: a server never says it lost a command, which is something only a client can find.
 */
export type CommandOutcomeStatus = "Completed" | "Failed" | "Pending" | "Unknown";

interface Sent<R> {
  /** The command's name on the wire. */
  readonly command: string;
  /** The id this client gave it, which its receipts carry and a pending one is asked for again by. */
  readonly id: string;
  /** The final outcome: this outcome itself, where it is final. Never rejects. */
  readonly completion: Promise<FinalOutcome<R>>;
}

/** Handled, with what the handler answered with. */
export interface CompletedOutcome<R> extends Sent<R> {
  readonly status: "Completed";
  readonly result: R;
}

/**
 * Accepted and then not done: the handler refused or failed, or the target was gone by the time the
 * command arrived. The reason is the one the server chose to show.
 */
export interface FailedOutcome<R> extends Sent<R> {
  readonly status: "Failed";
  readonly error: string;
}

/**
 * The connection the command was answered on ended, and the server no longer holds its outcome, or
 * would not answer for it when asked again. It may have run.
 */
export interface UnknownOutcome<R> extends Sent<R> {
  readonly status: "Unknown";
  readonly error: string;
}

/** Still being handled when the sender stopped waiting; listed as pending work until it lands. */
export interface PendingOutcome<R> extends Sent<R> {
  readonly status: "Pending";
}

export type FinalOutcome<R> = CompletedOutcome<R> | FailedOutcome<R> | UnknownOutcome<R>;

/** What sending a command came to. */
export type CommandOutcome<R> = FinalOutcome<R> | PendingOutcome<R>;

/** A command that did not complete, thrown by `resultOf` with its outcome. */
export class ScryCommandFailedError extends Error {
  readonly outcome: FailedOutcome<unknown> | UnknownOutcome<unknown>;

  constructor(outcome: FailedOutcome<unknown> | UnknownOutcome<unknown>) {
    super(outcome.error);
    this.name = "ScryCommandFailedError";
    this.outcome = outcome;
  }
}

/**
 * The result of a completed command. Throws `ScryCommandFailedError` where it failed or its outcome is
 * unknown, and an `Error` where it is still pending: await its `completion` first to wait for it.
 */
export function resultOf<R>(outcome: CommandOutcome<R>): R {
  if (outcome.status === "Completed") {
    return outcome.result;
  }

  if (outcome.status === "Pending") {
    throw new Error(`${outcome.command} is still pending: await its completion for the outcome.`);
  }

  throw new ScryCommandFailedError(outcome);
}

/** A command listed as pending work: from the moment it went pending until its outcome has been seen. */
export interface PendingCommand {
  readonly command: string;
  readonly id: string;
  /** The source a targeted command acts on. */
  readonly target: string | undefined;
  /** The target's key, as the payload gave it. */
  readonly keys: readonly unknown[];
  readonly sent: Date;
  readonly finished: Date | undefined;
  readonly status: CommandOutcomeStatus;
  readonly error: string | undefined;
  readonly completion: Promise<FinalOutcome<unknown>>;
}

export interface CommandsOptions {
  /**
   * How long sending waits for a command's outcome before answering it as pending and listing it as
   * pending work, in milliseconds. Three seconds unless set; `Infinity` waits however long it takes.
   */
  readonly wait?: number;
  /** When a pending command is asked for again after its connection ends. By default at once, then backing off to thirty seconds. */
  readonly retry?: RetryPolicy;
  /** How long a completed command stays listed after its outcome arrives, in milliseconds. Eight seconds unless set. */
  readonly completedLinger?: number;
}

export interface SendOptions {
  /**
   * Stops waiting. Before the server has answered it also abandons the request, so the command may or
   * may not have been accepted; after that it never stops the command, which goes on being followed
   * as pending work.
   */
  readonly signal?: AbortSignal;
}

type PayloadOf<T> = T extends scry.CommandTypes<infer P, unknown> ? P : never;
type ResultOf<T> = T extends scry.CommandTypes<unknown, infer R> ? R : never;

type Written<T> =
  T extends null ? null :
  T extends readonly (infer E)[] ? readonly Written<E>[] :
  Plain<T>;

/**
 * A payload as it may be written: each value as a query's constant may be, so a date as a `Date` or
 * its text, a decimal as a number or its text, and a [Flags] value as its names.
 */
export type PayloadInput<P> = { [K in keyof P]: Written<P[K]> };

/** One function per command, by its TypeScript name. */
export type Senders<C> = {
  readonly [K in keyof C]: (payload: PayloadInput<PayloadOf<C[K]>>, options?: SendOptions) => Promise<CommandOutcome<ResultOf<C[K]>>>;
};

interface Request {
  readonly name: string;
  readonly info: CommandInfo;
  readonly id: string;
  readonly payload: Record<string, unknown>;
}

interface Receipt {
  readonly status: (typeof ReceiptStatus)[number];
  readonly result: Json | undefined;
  readonly error: string | undefined;
}

type Mutable<T> = { -readonly [K in keyof T]: T[K] };

const closedMessage = "The commands were closed before the command's outcome arrived.";

export class ScryCommands<C> {
  /** Sends a command, answering with its outcome, or as pending where the server is still handling it. */
  readonly send: Senders<C>;
  readonly #client: ClientState;
  readonly #wait: number;
  readonly #retry: RetryPolicy;
  readonly #linger: number;
  readonly #closing = new AbortController();
  readonly #following = new Set<Promise<unknown>>();
  #capabilities: ReadonlySet<string> | undefined;
  #first: Promise<void> | undefined;
  readonly #capabilityListeners = new Set<() => void>();
  readonly #pending: Mutable<PendingCommand>[] = [];
  readonly #pendingListeners = new Set<() => void>();
  readonly #lingering = new Set<ReturnType<typeof setTimeout>>();

  constructor(sources: object, options: CommandsOptions = {}) {
    this.#client = clientOf(sources);
    this.#wait = options.wait ?? 3000;
    this.#retry = options.retry ?? this.#client.options.retry ?? backoff;
    this.#linger = options.completedLinger ?? 8000;
    if (this.#wait < 0) {
      throw new Error("wait must not be negative.");
    }

    const send: Record<string, unknown> = {};
    for (const name of Object.keys(this.#client.schema.commands)) {
      send[name] = (payload: unknown, sendOptions?: SendOptions) => this.#send(name, payload, sendOptions?.signal);
    }

    this.send = send as Senders<C>;
  }

  /**
   * Whether this caller may send the command at all, as the server last said. Advisory: the server
   * decides again on every command, and a targeted command's rows are decided by the `can{Command}`
   * member its target carries. False until the server has answered, which the first call starts.
   */
  can(command: keyof C & string): boolean {
    const known = this.#capabilities;
    const name = this.#client.schema.commands[command]?.name;
    if (known === undefined) {
      void this.ready;
      return false;
    }

    return name !== undefined && known.has(name);
  }

  /** Settles once the server has first said what this caller may send, or could not be asked. Never rejects. */
  get ready(): Promise<void> {
    this.#first ??= this.#readCapabilities(undefined).then(() => {}, () => {});
    return this.#first;
  }

  /** Calls the listener whenever what this caller may send changes, the first answer included. Returns its removal. */
  onCapabilitiesChanged(listener: () => void): () => void {
    this.#capabilityListeners.add(listener);
    return () => this.#capabilityListeners.delete(listener);
  }

  /** Asks the server again what this caller may send. Done after a denial too. */
  refreshCapabilities(signal?: AbortSignal): Promise<void> {
    return this.#readCapabilities(signal);
  }

  /** What is listed as pending work, oldest first: each command from going pending until its outcome has been seen. */
  get pending(): readonly PendingCommand[] {
    return this.#pending.map(_ => ({ ..._ }));
  }

  /** Calls the listener whenever pending work is listed, changes, or leaves. Returns its removal. */
  onPendingChanged(listener: () => void): () => void {
    this.#pendingListeners.add(listener);
    return () => this.#pendingListeners.delete(listener);
  }

  /** Drops every listed command that is no longer pending. A completed one leaves on its own; a failed or unknown one waits for this. */
  clearFinished(): void {
    const before = this.#pending.length;
    this.#remove(_ => _.status !== "Pending");
    if (this.#pending.length !== before) {
      this.#tell(this.#pendingListeners);
    }
  }

  /**
   * Stops following every pending command, each of which ends unknown, and settles once each has. No
   * command is stopped: the server finishes what it accepted whether anyone is still asking.
   */
  async close(): Promise<void> {
    this.#closing.abort();
    await Promise.all([...this.#following]);
    for (const timer of this.#lingering) {
      clearTimeout(timer);
    }

    this.#lingering.clear();
  }

  async #send(name: string, payload: unknown, signal: AbortSignal | undefined): Promise<CommandOutcome<unknown>> {
    if (this.#closing.signal.aborted) {
      throw new Error("These commands have been closed.");
    }

    const info = this.#client.schema.commands[name]!;
    const transport = this.#client.options.transport;
    if (transport?.command === undefined) {
      throw new Error("This client's transport does not send commands: httpTransport() does.");
    }

    signal?.throwIfAborted();
    const request: Request = { name, info, id: newId(), payload: payload as Record<string, unknown> };
    const json = serializeCommand(
      {
        version: CommandRequest.CurrentVersion,
        command: info.name,
        id: request.id,
        ...(this.#client.options.stamp === false ? {} : { stamp: this.#client.schema.stamp })
      },
      writePayload(info, payload, this.#client.scope.enumInfo));
    const started = Date.now();
    const sent = new Date();

    // Until the server has answered, the caller's signal is the command's too: nothing has been accepted
    // that abandoning the request would leave running. After that it stops only the waiting.
    const reading = new AbortController();
    const close = () => reading.abort();
    this.#closing.signal.addEventListener("abort", close, { once: true });
    const abandon = () => reading.abort(signal?.reason);
    signal?.addEventListener("abort", abandon, { once: true });

    let receipts: AsyncIterator<Answer> | undefined;
    let first: Receipt;
    try {
      receipts = transport.command(json, reading.signal)[Symbol.asyncIterator]();
      const next = await nextOf(receipts, reading.signal);
      if (next.done === true) {
        throw new ScryWireError("The command's connection ended before the server answered it, so whether it was accepted is unknown.");
      }

      first = this.#receipt(next.value);
    } catch (error) {
      reading.abort();
      done(receipts);
      this.#closing.signal.removeEventListener("abort", close);

      // A target that was not there when the command arrived is a race lost to whoever removed it: an
      // outcome to show, where every other failure before an answer is a refusal of the request itself.
      if (error instanceof ScryRequestError &&
          error.code === "NotFound") {
        return final(request, { status: "Failed", error: error.message });
      }

      // What this caller may do has moved since it was last read, or the button would not have been
      // there to press.
      if (error instanceof ScryPermissionError) {
        void this.#readCapabilities(undefined).catch(() => {});
      }

      signal?.throwIfAborted();
      throw error;
    } finally {
      signal?.removeEventListener("abort", abandon);
    }

    if (first.status !== "Pending") {
      done(receipts);
      reading.abort();
      this.#closing.signal.removeEventListener("abort", close);
      return this.#finished(request, first);
    }

    const follower = this.#follow(request, receipts, reading, close);
    this.#following.add(follower);
    let settled = false;
    void follower.then(() => {
      settled = true;
      this.#following.delete(follower);
    });

    const waited = await waitFor(follower, this.#wait - (Date.now() - started), signal);
    if (waited === "aborted") {
      // The caller stopped waiting and the command did not: it is followed where anyone can see.
      this.#list(request, sent, follower);
      signal!.throwIfAborted();
    }

    // Finished in the instant between the wait running out and here: an outcome in hand beats a pending
    // entry that completes as it is drawn.
    if (waited !== "timeout" ||
        settled) {
      return follower;
    }

    this.#list(request, sent, follower);
    return { command: info.name, id: request.id, status: "Pending", completion: follower };
  }

  /**
   * Reads a pending command's receipts to its outcome, asking for it again by id each time the
   * connection it is being answered on ends first. Never rejects: a command lost track of is an
   * unknown outcome.
   */
  async #follow(request: Request, first: AsyncIterator<Answer>, reading: AbortController, close: () => void): Promise<FinalOutcome<unknown>> {
    const transport = this.#client.options.transport!;
    let receipts: AsyncIterator<Answer> | undefined = first;
    let attempt = 1;
    let failures = 0;
    let quietSince = Date.now();
    try {
      while (true) {
        let failure: unknown;
        try {
          receipts ??= transport.receipt!(request.id, reading.signal)[Symbol.asyncIterator]();
          while (true) {
            const next = await nextOf(receipts, reading.signal);
            if (next.done === true) {
              break;
            }

            const receipt = this.#receipt(next.value);
            failures = 0;
            quietSince = Date.now();
            if (receipt.status !== "Pending") {
              return this.#finished(request, receipt);
            }
          }
        } catch (error) {
          if (reading.signal.aborted) {
            return final(request, { status: "Unknown", error: closedMessage });
          }

          const ending = endingOf(error, attempt > 1);
          if (ending === "lost") {
            return final(request, { status: "Unknown", error: "The server no longer holds this command's outcome. It may have run." });
          }

          if (ending === "refused") {
            return final(request, { status: "Unknown", error: `The server would not say what became of this command (${refusal(error)}). It may have run.` });
          }

          failure = error;
        } finally {
          done(receipts);
          receipts = undefined;
        }

        if (reading.signal.aborted) {
          return final(request, { status: "Unknown", error: closedMessage });
        }

        // Ended before the outcome, cut or ended by the server to bound its life, so it is asked for by
        // id, which answers the same stream while it runs and the outcome after.
        if (transport.receipt === undefined) {
          return final(request, { status: "Unknown", error: "The command's connection ended before its outcome, and this client's transport cannot ask for it again." });
        }

        const delay = this.#retry({ retries: failures, quietFor: Date.now() - quietSince, failure });
        if (delay === undefined) {
          return final(request, { status: "Unknown", error: "The command's connection ended before its outcome, and the retry policy declined to ask for it again." });
        }

        failures++;
        attempt++;
        if (delay > 0) {
          await wait(delay, reading.signal);
        }
      }
    } catch (error) {
      // A transport that throws where it should have answered: still an outcome, never a rejection of a
      // promise a screen is awaiting.
      return final(request, { status: "Unknown", error: (error as Error).message ?? String(error) });
    } finally {
      reading.abort();
      this.#closing.signal.removeEventListener("abort", close);
    }
  }

  #receipt(answer: Answer): Receipt {
    this.#client.observe(answer.stamp);
    const json = answer.json ?? parseJson(answer.text ?? "");
    if (!isObject(json) ||
        typeof json["status"] !== "string" ||
        !(ReceiptStatus as readonly string[]).includes(json["status"])) {
      throw new ScryWireError("A command was answered with a receipt this client cannot read.");
    }

    if (typeof json["stamp"] === "string") {
      this.#client.observe(json["stamp"]);
    }

    let error: string | undefined;
    if (typeof json["error"] === "string") {
      error = json["error"];
    }

    return { status: json["status"] as Receipt["status"], result: json["result"], error };
  }

  #finished(request: Request, receipt: Receipt): FinalOutcome<unknown> {
    if (receipt.status === "Completed") {
      return final(request, { status: "Completed", result: this.#result(request.info, receipt.result) });
    }

    return final(request, { status: "Failed", error: receipt.error ?? "The command failed." });
  }

  // Read as the description says the result is, a member the server left out as null.
  #result(info: CommandInfo, json: Json | undefined): unknown {
    if (info.result === undefined) {
      return undefined;
    }

    if (json === undefined) {
      throw new ScryWireError(`${info.name} completed without the ${info.result.name} it answers with.`);
    }

    const members: PlanMember[] = Object.entries(info.result.members).map(([key, member]) => {
      let plan: Plan = valuePlan(undefined);
      if (member.shape.kind === "value") {
        plan = valuePlan(member.shape.value);
      } else if (member.shape.kind === "values") {
        plan = { kind: "array", element: valuePlan(member.shape.value) };
      }

      return { key, wire: member.name, plan, optional: true };
    });
    const read = new Decoder(this.#client.scope, undefined).decode(json, { kind: "object", members }, `the result of ${info.name}`) as Record<string, unknown>;
    for (const member of members) {
      if (!Object.hasOwn(read, member.key)) {
        read[member.key] = null;
      }
    }

    return read;
  }

  async #readCapabilities(signal: AbortSignal | undefined): Promise<void> {
    const transport: Transport | undefined = this.#client.options.transport;
    if (transport?.capabilities === undefined) {
      this.#setCapabilities([]);
      return;
    }

    let answer: Answer | undefined;
    try {
      answer = await transport.capabilities(signal);
    } catch (error) {
      if (signal?.aborted === true) {
        throw error;
      }

      // Advisory: failing to read it leaves what was known, or nothing, as it was.
      return;
    }

    // No route at all is a server with commands off, which allows nothing.
    if (answer === undefined) {
      this.#setCapabilities([]);
      return;
    }

    this.#client.observe(answer.stamp);
    let json: Json;
    try {
      json = answer.json ?? parseJson(answer.text ?? "");
    } catch {
      return;
    }

    if (!isObject(json) ||
        !Array.isArray(json["commands"])) {
      return;
    }

    if (typeof json["stamp"] === "string") {
      this.#client.observe(json["stamp"]);
    }

    this.#setCapabilities(json["commands"].filter(_ => typeof _ === "string"));
  }

  #setCapabilities(commands: readonly string[]): void {
    const next = new Set(commands);
    const previous = this.#capabilities;
    this.#capabilities = next;
    if (previous !== undefined &&
        previous.size === next.size &&
        [...next].every(_ => previous.has(_))) {
      return;
    }

    this.#tell(this.#capabilityListeners);
  }

  #list(request: Request, sent: Date, follower: Promise<FinalOutcome<unknown>>): void {
    const entry: Mutable<PendingCommand> = {
      command: request.info.name,
      id: request.id,
      target: request.info.target,
      keys: (request.info.keys ?? []).map(_ => request.payload[camelCase(_)]),
      sent,
      finished: undefined,
      status: "Pending",
      error: undefined,
      completion: follower
    };
    this.#pending.push(entry);
    this.#tell(this.#pendingListeners);
    void follower.then(outcome => this.#finish(entry, outcome));
  }

  #finish(entry: Mutable<PendingCommand>, outcome: FinalOutcome<unknown>): void {
    entry.status = outcome.status;
    entry.finished = new Date();
    if (outcome.status !== "Completed") {
      entry.error = outcome.error;
    }

    this.#tell(this.#pendingListeners);
    if (outcome.status !== "Completed") {
      return;
    }

    // Seen to finish rather than vanishing; a failed or unknown one stays until it is cleared, since it
    // is saying something somebody should read.
    if (this.#linger <= 0) {
      this.#drop(entry);
      return;
    }

    const timer = setTimeout(() => {
      this.#lingering.delete(timer);
      this.#drop(entry);
    }, this.#linger);
    (timer as { unref?: () => void }).unref?.();
    this.#lingering.add(timer);
  }

  #drop(entry: Mutable<PendingCommand>): void {
    const index = this.#pending.indexOf(entry);
    if (index < 0) {
      return;
    }

    this.#pending.splice(index, 1);
    this.#tell(this.#pendingListeners);
  }

  #remove(predicate: (entry: Mutable<PendingCommand>) => boolean): void {
    for (let index = this.#pending.length - 1; index >= 0; index--) {
      if (predicate(this.#pending[index]!)) {
        this.#pending.splice(index, 1);
      }
    }
  }

  #tell(listeners: ReadonlySet<() => void>): void {
    for (const listener of listeners) {
      try {
        listener();
      } catch {
        // A screen that throws while redrawing must not stop the next one being told.
      }
    }
  }
}

/**
 * The commands of the client `sources` came from: one function per command on `send`, what this
 * caller may send, and what is still pending.
 */
export function createCommands<C>(sources: object, options?: CommandsOptions): ScryCommands<C> {
  return new ScryCommands<C>(sources, options);
}

// The next receipt, or the abort: a transport that does not end its connection when told to still
// cannot hold a command's follower, or the sender, past being closed.
function nextOf<T>(receipts: AsyncIterator<T>, signal: AbortSignal): Promise<IteratorResult<T>> {
  if (signal.aborted) {
    return Promise.reject(signal.reason);
  }

  return new Promise((resolve, reject) => {
    const aborted = () => reject(signal.reason);
    signal.addEventListener("abort", aborted, { once: true });
    receipts.next().then(
      next => {
        signal.removeEventListener("abort", aborted);
        resolve(next);
      },
      error => {
        signal.removeEventListener("abort", aborted);
        reject(error);
      });
  });
}

// Ends a connection's receipts. Awaited nowhere: a generator still waiting inside its transport
// finishes its own step before it can return, and that wait is the transport's, not the command's.
function done(receipts: AsyncIterator<unknown> | undefined): void {
  receipts?.return?.()?.catch(() => {});
}

type Decided =
  | { readonly status: "Completed"; readonly result: unknown }
  | { readonly status: "Failed" | "Unknown"; readonly error: string };

function final(request: Request, decided: Decided): FinalOutcome<unknown> {
  const outcome = { command: request.info.name, id: request.id, ...decided } as Mutable<FinalOutcome<unknown>>;
  outcome.completion = Promise.resolve(outcome);
  return outcome;
}

// Whether the follower settled within the wait, the wait ran out, or the caller stopped waiting.
function waitFor(follower: Promise<unknown>, milliseconds: number, signal: AbortSignal | undefined): Promise<"settled" | "timeout" | "aborted"> {
  if (signal?.aborted === true) {
    return Promise.resolve("aborted");
  }

  return new Promise(resolve => {
    let timer: ReturnType<typeof setTimeout> | undefined;
    const done = (how: "settled" | "timeout" | "aborted") => {
      clearTimeout(timer);
      signal?.removeEventListener("abort", aborted);
      resolve(how);
    };
    const aborted = () => done("aborted");
    signal?.addEventListener("abort", aborted, { once: true });
    if (Number.isFinite(milliseconds)) {
      timer = setTimeout(() => done("timeout"), Math.max(0, milliseconds));
    }

    void follower.then(() => done("settled"));
  });
}

// What ended a connection a pending command was being answered on. The command was accepted before any
// of these, so none can say it was not done (only a receipt says that), and what asking again would not
// get past leaves the outcome unknown.
function endingOf(error: unknown, reattached: boolean): "again" | "lost" | "refused" {
  // Asked for by id and not found: pruned, never held on this node, or asked of a server that no longer
  // serves commands at all.
  if (reattached &&
      ((error instanceof ScryRequestError && error.status === 404) || error instanceof ScryNotServedError)) {
    return "lost";
  }

  if (error instanceof ScryRequestError) {
    // About the moment rather than the command.
    if (error.code === "ExecutionFailed") {
      return "again";
    }

    if (error.code === "Unknown" &&
        (error.status >= 500 || error.status === 408 || error.status === 429)) {
      return "again";
    }

    // A rejection asking again would meet again: an expired token's 401, a denial.
    return "refused";
  }

  // fetch's own failures: the connection could not be made, or was cut.
  if (error instanceof TypeError ||
      error instanceof DOMException) {
    return "again";
  }

  return "refused";
}

// Why a server would not answer for a command, as a person reads it: the endpoint's own message where
// there is one, and otherwise the status alone, since a gateway's page is not a reason.
function refusal(error: unknown): string {
  if (!(error instanceof ScryRequestError)) {
    return (error as Error).message ?? String(error);
  }

  if (error.code === "Unknown") {
    return `answered ${error.status}`;
  }

  return error.message;
}

// A version 4 UUID, from getRandomValues, which a page served over plain HTTP still has where
// randomUUID is not.
function newId(): string {
  const bytes = crypto.getRandomValues(new Uint8Array(16));
  bytes[6] = (bytes[6]! & 0x0f) | 0x40;
  bytes[8] = (bytes[8]! & 0x3f) | 0x80;
  const hex = [...bytes].map(_ => _.toString(16).padStart(2, "0")).join("");
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
