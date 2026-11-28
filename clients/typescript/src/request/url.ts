// How a request travels: in a URL where it can, which is what lets a cache between client and server
// store the answer, and as a body where it cannot. The URL form is base64url of the request's UTF-8
// bytes, unpadded, in one `q` parameter (QueryUrl.Encode on the .NET side).

import { QueryUrl } from "../wire.ts";

const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_";

/** Bytes as base64url, unpadded. */
export function base64Url(bytes: Uint8Array): string {
  let out = "";
  for (let index = 0; index < bytes.length; index += 3) {
    const first = bytes[index]!;
    const second = bytes[index + 1];
    const third = bytes[index + 2];
    out += alphabet.charAt(first >> 2);
    out += alphabet.charAt(((first & 0x03) << 4) | ((second ?? 0) >> 4));
    if (second === undefined) {
      break;
    }

    out += alphabet.charAt(((second & 0x0f) << 2) | ((third ?? 0) >> 6));
    if (third === undefined) {
      break;
    }

    out += alphabet.charAt(third & 0x3f);
  }

  return out;
}

/** A serialized request as the value of the URL form's parameter. */
export function encodeQuery(json: string): string {
  return base64Url(new TextEncoder().encode(json));
}

export type Form =
  | { readonly kind: "url"; readonly query: string }
  | { readonly kind: "body"; readonly body: string };

/**
 * The form a serialized request travels in: a URL when its encoding fits the server's limit (which every
 * response advertises, and introspection carries before any has been read), a body otherwise. A query
 * reading a member the model marks sensitive always travels as a body. The server requires that only
 * where such a member is compared with a constant, so this is the cautious side of its rule; the bytes
 * of the request are the same either way.
 */
export function formFor(json: string, sensitive: boolean, urlLimit: number = QueryUrl.MaxLength): Form {
  if (sensitive ||
      urlLimit <= 0) {
    return { kind: "body", body: json };
  }

  const query = encodeQuery(json);
  if (query.length > urlLimit) {
    return { kind: "body", body: json };
  }

  return { kind: "url", query };
}
