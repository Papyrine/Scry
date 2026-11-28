// Sends requests to the endpoint MapScry mapped, over fetch: as a URL where the request fits in one and
// reads nothing sensitive, which is what lets a cache between here and the server keep the answer, and
// as a body otherwise. What the server says about itself on every response, its schema stamp and its
// URL budget, is read off whichever response says it.

import { QueryUrl, WireFormat, type ScryError, type ScryErrorCode } from "./wire.ts";
import type { QueryRequest } from "./wire.ts";
import { ScryPermissionError, ScryRequestError } from "./errors.ts";
import { formFor, type Form } from "./request/url.ts";
import { parseJson, RawNumber } from "./response/json.ts";
import type { Answer, RequestContext, Transport } from "./query/query.ts";

export interface HttpOptions {
  /** The endpoint MapScry was given: absolute, or relative to a page's own origin in a browser. */
  readonly endpoint: string | URL;
  /** The fetch to send with. The global one by default. */
  readonly fetch?: typeof fetch;
  /** Headers every request carries, such as an authorization; a function is asked each time. */
  readonly headers?: Readonly<Record<string, string>> | (() => Readonly<Record<string, string>> | Promise<Readonly<Record<string, string>>>);
}

const codes: readonly ScryErrorCode[] = [
  "Unknown",
  "WireFormat",
  "Validation",
  "StaleClient",
  "Forbidden",
  "UnsupportedMedia",
  "ExecutionFailed",
  "SubscriptionLimit",
  "NotFound",
  "PayloadTooLarge",
  "CommandLimit"
];

export function httpTransport(options: HttpOptions): Transport {
  const send = options.fetch ?? globalThis.fetch;
  const endpoint = String(options.endpoint);
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

  async function ask(form: Form): Promise<Response> {
    const configured = await headers();
    if (form.kind === "url") {
      const separator = endpoint.includes("?") ? "&" : "?";
      return send(`${endpoint}${separator}${QueryUrl.Parameter}=${form.query}`, { method: "GET", headers: configured });
    }

    return send(endpoint, {
      method: "POST",
      headers: { ...configured, "Content-Type": "application/json; charset=utf-8" },
      body: form.body
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

  return {
    async query(_request: QueryRequest, context: RequestContext): Promise<Answer> {
      let form = formFor(context.json, context.sensitive, limit(context));
      let response = await ask(form);
      let stamp = learn(response);

      // A URL refused before it reached the server carries no budget to learn from, so the query is
      // asked again as a body, and nothing as long is asked as a URL again: nothing at all after a 405,
      // which says no URL form is mapped here.
      if (form.kind === "url") {
        const ceiling = urlCeiling(response, form.query.length);
        if (ceiling !== undefined) {
          ingress = Math.min(ingress, ceiling);
          form = { kind: "body", body: context.json };
          response = await ask(form);
          stamp = learn(response) ?? stamp;
        }
      }

      let text = await response.text();

      // The server marks a member sensitive that this client was generated before, and refuses the
      // URL for it: worth one more round trip, as the same request in a body.
      if (form.kind === "url" &&
          !response.ok &&
          error(text)?.requiresBody === true) {
        form = { kind: "body", body: context.json };
        response = await ask(form);
        stamp = learn(response) ?? stamp;
        text = await response.text();
      }

      if (!response.ok) {
        throw failure(response.status, text, stamp);
      }

      if (stamp === undefined) {
        return { text };
      }

      return { text, stamp };
    }
  };
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

function error(text: string): ScryError | undefined {
  try {
    const parsed = parseJson(text);
    if (typeof parsed !== "object" ||
        parsed === null ||
        Array.isArray(parsed) ||
        parsed instanceof RawNumber ||
        typeof parsed["error"] !== "string") {
      return undefined;
    }

    const code = parsed["code"];
    const known = typeof code === "string" && (codes as readonly string[]).includes(code);
    return {
      error: parsed["error"],
      ...(known ? { code: code as ScryErrorCode } : {}),
      ...(parsed["requiresBody"] === true ? { requiresBody: true } : {})
    };
  } catch {
    return undefined;
  }
}

function failure(status: number, text: string, stamp: string | undefined): Error {
  const refused = error(text);
  if (refused === undefined) {
    let message = text;
    if (message.length === 0) {
      message = `The server answered ${status}.`;
    }

    return new ScryRequestError(status, "Unknown", message, false, stamp);
  }

  if (refused.code === "Forbidden") {
    return new ScryPermissionError(status, refused.error, stamp);
  }

  return new ScryRequestError(status, refused.code ?? "Unknown", refused.error, refused.requiresBody === true, stamp);
}
