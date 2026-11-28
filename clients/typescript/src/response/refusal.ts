// A ScryError body, read wherever one arrives: a refused response, a stream's error marker, a live
// query's error event, a batch's failed entry.

import type { ScryErrorCode } from "../wire.ts";
import type { Refusal } from "../errors.ts";
import { RawNumber, type Json } from "./json.ts";

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

export function isObject(json: Json | undefined): json is { [key: string]: Json } {
  return typeof json === "object" &&
         json !== null &&
         !Array.isArray(json) &&
         !(json instanceof RawNumber);
}

/** The refusal a body carries, or nothing where it is not one: a proxy's own page, say. */
export function refusalIn(json: Json | undefined): Refusal | undefined {
  if (!isObject(json) ||
      typeof json["error"] !== "string") {
    return undefined;
  }

  const code = json["code"];
  const known = typeof code === "string" && (codes as readonly string[]).includes(code);
  return {
    error: json["error"],
    ...(known ? { code: code as ScryErrorCode } : {}),
    ...(json["requiresBody"] === true ? { requiresBody: true } : {})
  };
}
