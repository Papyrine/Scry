// Sends requests to the endpoint MapScry mapped, over fetch: as a URL where the request fits in one and
// reads nothing sensitive, which is what lets a cache between here and the server keep the answer, and
// as a body otherwise. What the server says about itself on every response, its schema stamp and its
// URL budget, is read off whichever response says it. Beside the query endpoint MapScry maps
// `…/batch`, `…/stream` (newline-delimited JSON) and `…/subscribe` (server-sent events), and, where
// commands are on, `…/command`, `…/command/{id}` and `…/capabilities`.

import { QueryUrl, ScryBinary, ScryCommandProtocol, ScryLive, ScryStream, WireFormat } from "./wire.ts";
import type { QueryRequest } from "./wire.ts";
import { refused, ScryNotServedError, ScryRequestError, ScryWireError, statusFor } from "./errors.ts";
import { formFor, type Form } from "./request/url.ts";
import { parseJson } from "./response/json.ts";
import { boundaryOf, isBinary, readMultipart, sections } from "./response/multipart.ts";
import { refusalIn } from "./response/refusal.ts";
import type { Answer, AttachmentAnswer, LiveContext, LiveEvent, RequestContext, StreamLine, Transport } from "./query/query.ts";

export interface HttpOptions {
  /** The endpoint MapScry was given: absolute, or relative to a page's own origin in a browser. */
  readonly endpoint: string | URL;
  /** The fetch to send with. The global one by default. */
  readonly fetch?: typeof fetch;
  /** Headers every request carries, such as an authorization; a function is asked each time. */
  readonly headers?: Readonly<Record<string, string>> | (() => Readonly<Record<string, string>> | Promise<Readonly<Record<string, string>>>);
}

const json = "application/json; charset=utf-8";

// A command is answered as JSON where the server decided it at once, and as events where it did not.
const receiptTypes = `application/json, ${ScryLive.ContentType}`;

export function httpTransport(options: HttpOptions): Transport {
  const send = options.fetch ?? globalThis.fetch;
  const endpoint = String(options.endpoint);
  const beside = (route: string) => `${endpoint.replace(/\/+$/, "")}/${route}`;
  // The longest URL this transport asks with: what the server advertises, never past what a refusal
  // in front of it proved.
  let advertised: number | undefined;
  let ingress = Number.POSITIVE_INFINITY;

  async function headers(): Promise<Record<string, string>> {
    if (typeof options.headers === "function") {
      return { ...await options.headers() };
    }

    return { ...options.headers };
  }

  async function ask(form: Form, signal: AbortSignal | undefined): Promise<Response> {
    const configured = await headers();
    if (form.kind === "url") {
      const separator = endpoint.includes("?") ? "&" : "?";
      return send(`${endpoint}${separator}${QueryUrl.Parameter}=${form.query}`, { method: "GET", headers: configured, ...signalled(signal) });
    }

    return post(endpoint, form.body, {}, signal);
  }

  async function post(url: string, body: string, extra: Record<string, string>, signal: AbortSignal | undefined): Promise<Response> {
    return send(url, {
      method: "POST",
      headers: { ...await headers(), ...extra, "Content-Type": json },
      body,
      ...signalled(signal)
    });
  }

  // What the server says about itself, from any response: its refusals carry it too.
  function learn(response: Response): string | undefined {
    const limit = response.headers.get(WireFormat.UrlLimitHeader);
    if (limit !== null &&
        /^\d+$/.test(limit)) {
      advertised = Number(limit);
    }

    return response.headers.get(WireFormat.SchemaStampHeader) ?? undefined;
  }

  function limit(context: RequestContext): number {
    return Math.min(advertised ?? context.urlLimit, ingress);
  }

  async function refuse(response: Response, stamp: string | undefined): Promise<never> {
    throw failure(response.status, await response.text(), stamp);
  }

  // A response's JSON, or where it carries binary parts, its parts and then the envelope after them.
  async function read(response: Response, stamp: string | undefined): Promise<Answer> {
    if (!response.ok) {
      return refuse(response, stamp);
    }

    if (mediaType(response) !== ScryBinary.ContentType) {
      return answered(await response.text(), stamp);
    }

    const { envelope, parts } = await readMultipart(bodyOf(response), boundaryOf(response.headers.get("Content-Type")!));
    return { ...answered(envelope, stamp), parts };
  }

  async function get(url: string, accept: string, signal: AbortSignal | undefined): Promise<Response> {
    return send(url, { method: "GET", headers: { ...await headers(), Accept: accept }, ...signalled(signal) });
  }

  // A command's receipts, however they were answered: one as JSON, or a stream of them as events,
  // framed as a live query's answers are and ended by the outcome, by an end the server sent to bound
  // the stream's life, or cut.
  async function* receipts(response: Response, bareRefusalIsNotServing: boolean, signal: AbortSignal): AsyncIterable<Answer> {
    const stamp = learn(response);
    if (!response.ok) {
      const text = await response.text();

      // No such route, and no word from the endpoint: the server maps none, since commands are off. A
      // 404 with the endpoint's own body is about the target, not the route.
      if (bareRefusalIsNotServing &&
          (response.status === 404 || response.status === 405) &&
          (refusalIn(tryParse(text))?.error ?? "").length === 0) {
        throw notServingCommands();
      }

      throw failure(response.status, text, stamp);
    }

    const type = mediaType(response);
    if (type === "application/json") {
      yield answered(await response.text(), stamp);
      return;
    }

    // A success that is neither is somebody else's answer: a single-page app's fallback route serving
    // its index page for a path the server does not map, usually.
    if (type !== ScryLive.ContentType) {
      throw notServingCommands();
    }

    for await (const event of events(lines(response, signal))) {
      if (event.event === ScryLive.Result) {
        yield answered(event.data, stamp);
        continue;
      }

      if (event.event === ScryLive.Error) {
        throw failure(statusFor(refusalIn(tryParse(event.data))?.code), event.data, stamp);
      }

      if (event.event === ScryLive.End) {
        return;
      }

      // A heartbeat, or an event from a server newer than this client.
    }
  }

  return {
    async query(_request: QueryRequest, context: RequestContext): Promise<Answer> {
      let form = formFor(context.json, context.sensitive, limit(context));
      let response = await ask(form, context.signal);
      let stamp = learn(response);

      // A URL refused before it reached the server carries no budget to learn from, so the query is
      // asked again as a body, and nothing as long is asked as a URL again: nothing at all after a 405,
      // which says no URL form is mapped here.
      if (form.kind === "url") {
        const ceiling = urlCeiling(response, form.query.length);
        if (ceiling !== undefined) {
          ingress = Math.min(ingress, ceiling);
          form = { kind: "body", body: context.json };
          response = await ask(form, context.signal);
          stamp = learn(response) ?? stamp;
        }
      }

      // The server marks a member sensitive that this client was generated before, and refuses the
      // URL for it: worth one more round trip, as the same request in a body.
      if (form.kind === "url" &&
          !response.ok) {
        const text = await response.text();
        if (refusalIn(tryParse(text))?.requiresBody !== true) {
          throw failure(response.status, text, stamp);
        }

        form = { kind: "body", body: context.json };
        response = await ask(form, context.signal);
        stamp = learn(response) ?? stamp;
      }

      return read(response, stamp);
    },

    async batch(body: string, signal?: AbortSignal): Promise<Answer> {
      const response = await post(beside("batch"), body, {}, signal);
      return read(response, learn(response));
    },

    async *stream(_request: QueryRequest, context: RequestContext): AsyncIterable<string | StreamLine> {
      const connection = linked(context.signal);
      try {
        const response = await post(beside("stream"), context.json, {}, connection.signal);
        const stamp = learn(response);
        if (!response.ok) {
          await refuse(response, stamp);
        }

        const type = mediaType(response);
        if (type === ScryBinary.ContentType) {
          yield* streamedParts(bodyOf(response), boundaryOf(response.headers.get("Content-Type")!), connection.signal);
          return;
        }

        if (type !== ScryStream.ContentType) {
          throw new ScryWireError(`The stream was answered as '${type}', not newline-delimited JSON.`);
        }

        yield* lines(response, connection.signal);
      } finally {
        connection.abort();
      }
    },

    async *subscribe(_request: QueryRequest, context: LiveContext): AsyncIterable<LiveEvent> {
      const connection = linked(context.signal);
      try {
        const extra: Record<string, string> = { Accept: ScryLive.ContentType };
        if (context.lastEventId !== undefined) {
          extra[ScryLive.LastEventIdHeader] = context.lastEventId;
        }

        const response = await post(beside(ScryLive.Route), context.json, extra, connection.signal);
        const stamp = learn(response);
        if (response.status === 404 ||
            response.status === 405) {
          throw new ScryNotServedError("This server is not serving live queries: ScryOptions.MaxSubscriptions maps the route, at how many it may hold open at once.");
        }

        if (!response.ok) {
          await refuse(response, stamp);
        }

        if (mediaType(response) !== ScryLive.ContentType) {
          throw new ScryNotServedError("This server is not serving live queries: the route did not answer with an event stream.");
        }

        yield* events(lines(response, connection.signal));
      } finally {
        connection.abort();
      }
    },

    async *command(body: string, signal: AbortSignal): AsyncIterable<Answer> {
      const connection = linked(signal);
      try {
        const response = await post(beside(ScryCommandProtocol.Route), body, { Accept: receiptTypes }, connection.signal);
        yield* receipts(response, true, connection.signal);
      } finally {
        connection.abort();
      }
    },

    async *receipt(id: string, signal: AbortSignal): AsyncIterable<Answer> {
      const connection = linked(signal);
      try {
        const response = await get(beside(`${ScryCommandProtocol.Route}/${encodeURIComponent(id)}`), receiptTypes, connection.signal);
        yield* receipts(response, false, connection.signal);
      } finally {
        connection.abort();
      }
    },

    async attachment(body: string, signal?: AbortSignal): Promise<AttachmentAnswer> {
      const response = await post(beside("attachment"), body, {}, signal);
      const stamp = learn(response);
      const stamped: { stamp?: string } = stamp === undefined ? {} : { stamp };

      // The row was readable and the value is null.
      if (response.status === 204) {
        await response.body?.cancel();
        return stamped;
      }

      // Refused, absent or hidden by a row policy, which the server answers alike and with nothing.
      if (response.status === 404 &&
          (await response.clone().text()).length === 0) {
        throw new ScryRequestError(404, "Unknown", "The server would not hand the attachment over: its row is absent, hidden by a policy, or refused by the attachment's check, which it answers alike.", false, stamp);
      }

      if (!response.ok) {
        return refuse(response, stamp);
      }

      const length = response.headers.get("Content-Length");
      return {
        ...stamped,
        content: {
          contentType: response.headers.get("Content-Type") ?? ScryBinary.PartContentType,
          length: length === null ? undefined : Number(length),
          body: response.body ?? new ReadableStream({ start: _ => _.close() })
        }
      };
    },

    async capabilities(signal?: AbortSignal): Promise<Answer | undefined> {
      const response = await get(beside(ScryCommandProtocol.CapabilitiesRoute), "application/json", signal);
      const stamp = learn(response);

      // No route: commands are off, and there is nothing this caller may send.
      if (response.status === 404 ||
          response.status === 405) {
        return undefined;
      }

      const text = await response.text();
      if (!response.ok) {
        throw failure(response.status, text, stamp);
      }

      if (mediaType(response) !== "application/json") {
        return undefined;
      }

      return answered(text, stamp);
    }
  };
}

function notServingCommands(): ScryNotServedError {
  return new ScryNotServedError("This server is not serving commands: ScryOptions.MaxPendingCommands maps the routes, at how many it may have in flight at once.");
}

function signalled(signal: AbortSignal | undefined): { signal?: AbortSignal } {
  if (signal === undefined) {
    return {};
  }

  return { signal };
}

// A connection of its own, ended when the caller's signal is, for its reason, or when what reads it
// stops reading.
function linked(signal: AbortSignal | undefined): AbortController {
  const connection = new AbortController();
  if (signal === undefined) {
    return connection;
  }

  if (signal.aborted) {
    connection.abort(signal.reason);
    return connection;
  }

  const forward = () => connection.abort(signal.reason);
  signal.addEventListener("abort", forward, { once: true });
  // Each reader aborts its connection once done with it, which lets go of a signal that outlives it.
  connection.signal.addEventListener("abort", () => signal.removeEventListener("abort", forward), { once: true });
  return connection;
}

function bodyOf(response: Response): ReadableStream<Uint8Array> {
  if (response.body === null) {
    throw new ScryWireError("A multipart response arrived with no body.");
  }

  return response.body;
}

// A stream that carries binary parts: sections of lines, markers and rows, between which come the parts
// the next row references. A row's placeholders index the parts since the line before it.
async function* streamedParts(body: ReadableStream<Uint8Array>, boundary: string, signal: AbortSignal): AsyncIterable<StreamLine> {
  let parts: Uint8Array[] = [];
  for await (const section of sections(body, boundary, signal)) {
    if (isBinary(section)) {
      parts.push(section.body);
      continue;
    }

    if (section.type !== ScryStream.ContentType) {
      throw new ScryWireError(`A streamed multipart section was '${section.type}', neither a binary part nor lines.`);
    }

    for (const line of new TextDecoder().decode(section.body).split("\n")) {
      const text = line.replace(/\r$/, "");
      if (text.length === 0) {
        continue;
      }

      yield { line: text, parts };
      parts = [];
    }
  }
}

function mediaType(response: Response): string {
  return (response.headers.get("Content-Type") ?? "").split(";")[0]!.trim().toLowerCase();
}

function answered(text: string, stamp: string | undefined): Answer {
  if (stamp === undefined) {
    return { text };
  }

  return { text, stamp };
}

function tryParse(text: string) {
  try {
    return parseJson(text);
  } catch {
    return undefined;
  }
}

function urlCeiling(response: Response, length: number): number | undefined {
  if (response.status === 405) {
    return 0;
  }

  if (response.headers.has(WireFormat.UrlLimitHeader)) {
    return undefined;
  }

  if (response.status === 400 ||
      response.status === 404 ||
      response.status === 414 ||
      response.status === 431) {
    return length - 1;
  }

  return undefined;
}

function failure(status: number, text: string, stamp: string | undefined): Error {
  const refusal = refusalIn(tryParse(text));
  if (refusal === undefined) {
    let message = text;
    if (message.length === 0) {
      message = `The server answered ${status}.`;
    }

    return new ScryRequestError(status, "Unknown", message, false, stamp);
  }

  return refused(status, refusal, stamp);
}

/** A body's lines as they arrive, each without its line break; one still open when it ends is the last. */
export async function* lines(response: Response, signal?: AbortSignal): AsyncIterable<string> {
  if (response.body === null) {
    return;
  }

  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let pending = "";
  try {
    while (true) {
      if (signal?.aborted === true) {
        return;
      }

      const { done, value } = await reader.read();
      if (done) {
        break;
      }

      pending += decoder.decode(value, { stream: true });
      let start = 0;
      for (let index = 0; index < pending.length; index++) {
        const code = pending.charCodeAt(index);
        if (code !== 0x0a &&
            code !== 0x0d) {
          continue;
        }

        // A CR LF pair is one break, even when the LF has not arrived yet.
        if (code === 0x0d &&
            index === pending.length - 1) {
          break;
        }

        yield pending.slice(start, index);
        if (code === 0x0d &&
            pending.charCodeAt(index + 1) === 0x0a) {
          index++;
        }

        start = index + 1;
      }

      pending = pending.slice(start);
    }

    pending += decoder.decode();
    if (pending.length > 0) {
      yield pending.replace(/\r$/, "");
    }
  } finally {
    reader.releaseLock();
  }
}

/**
 * Server-sent events from a stream's lines, as the EventSource specification reads them: a field per
 * line, an event at each blank line, and data lines joined by line breaks. An event whose data is
 * empty is still an event where it carried a `data:` line, which is why every Scry event carries one.
 * One still open when the lines end was cut short, and is not one.
 */
export async function* events(source: AsyncIterable<string>): AsyncIterable<LiveEvent> {
  let type = "";
  let id: string | undefined;
  let data: string | undefined;
  for await (const line of source) {
    if (line.length === 0) {
      if (data !== undefined) {
        const event: LiveEvent = { event: type || "message", data };
        yield id === undefined ? event : { ...event, id };
      }

      type = "";
      id = undefined;
      data = undefined;
      continue;
    }

    if (line.startsWith(":")) {
      continue;
    }

    const colon = line.indexOf(":");
    let field = line;
    let value = "";
    if (colon >= 0) {
      field = line.slice(0, colon);
      value = line.slice(colon + 1);
      if (value.startsWith(" ")) {
        value = value.slice(1);
      }
    }

    switch (field) {
      case "event":
        type = value;
        break;
      case "id":
        id = value;
        break;
      case "data":
        data = data === undefined ? value : `${data}\n${value}`;
        break;
      default:
        break;
    }
  }
}
