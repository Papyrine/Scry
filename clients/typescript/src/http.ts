// Sends requests to the endpoint MapScry mapped, over fetch: as a URL where the request fits in one and
// reads nothing sensitive, which is what lets a cache between here and the server keep the answer, and
// as a body otherwise. What the server says about itself on every response, its schema stamp and its
// URL budget, is read off whichever response says it. Beside the query endpoint MapScry maps
// `…/batch`, `…/stream` (newline-delimited JSON) and `…/subscribe` (server-sent events).

import { QueryUrl, ScryBinary, ScryLive, ScryStream, WireFormat } from "./wire.ts";
import type { QueryRequest } from "./wire.ts";
import { refused, ScryRequestError, ScryWireError } from "./errors.ts";
import { formFor, type Form } from "./request/url.ts";
import { parseJson } from "./response/json.ts";
import { refusalIn } from "./response/refusal.ts";
import type { Answer, LiveContext, LiveEvent, RequestContext, Transport } from "./query/query.ts";

export interface HttpOptions {
  /** The endpoint MapScry was given: absolute, or relative to a page's own origin in a browser. */
  readonly endpoint: string | URL;
  /** The fetch to send with. The global one by default. */
  readonly fetch?: typeof fetch;
  /** Headers every request carries, such as an authorization; a function is asked each time. */
  readonly headers?: Readonly<Record<string, string>> | (() => Readonly<Record<string, string>> | Promise<Readonly<Record<string, string>>>);
}

const json = "application/json; charset=utf-8";

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

      let text = await response.text();

      // The server marks a member sensitive that this client was generated before, and refuses the
      // URL for it: worth one more round trip, as the same request in a body.
      if (form.kind === "url" &&
          !response.ok &&
          refusalIn(tryParse(text))?.requiresBody === true) {
        form = { kind: "body", body: context.json };
        response = await ask(form, context.signal);
        stamp = learn(response) ?? stamp;
        text = await response.text();
      }

      if (!response.ok) {
        throw failure(response.status, text, stamp);
      }

      return answered(text, stamp);
    },

    async batch(body: string, signal?: AbortSignal): Promise<Answer> {
      const response = await post(beside("batch"), body, {}, signal);
      const stamp = learn(response);
      if (!response.ok) {
        return refuse(response, stamp);
      }

      return answered(await response.text(), stamp);
    },

    async *stream(_request: QueryRequest, context: RequestContext): AsyncIterable<string> {
      const connection = linked(context.signal);
      try {
        const response = await post(beside("stream"), context.json, {}, connection.signal);
        const stamp = learn(response);
        if (!response.ok) {
          await refuse(response, stamp);
        }

        const type = mediaType(response);
        if (type === ScryBinary.ContentType) {
          throw new ScryWireError("The stream carries binary parts, in multipart, which this client does not read yet.");
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
          throw new Error("This server is not serving live queries: ScryOptions.MaxSubscriptions maps the route, at how many it may hold open at once.");
        }

        if (!response.ok) {
          await refuse(response, stamp);
        }

        if (mediaType(response) !== ScryLive.ContentType) {
          throw new Error("This server is not serving live queries: the route did not answer with an event stream.");
        }

        yield* events(lines(response, connection.signal));
      } finally {
        connection.abort();
      }
    }
  };
}

function signalled(signal: AbortSignal | undefined): { signal?: AbortSignal } {
  if (signal === undefined) {
    return {};
  }

  return { signal };
}

// A connection of its own, ended when the caller's signal is or when what reads it stops reading.
function linked(signal: AbortSignal | undefined): AbortController {
  const connection = new AbortController();
  if (signal?.aborted === true) {
    connection.abort();
  } else {
    signal?.addEventListener("abort", () => connection.abort(), { once: true });
  }

  return connection;
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
