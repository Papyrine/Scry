// Reads the multipart/mixed a result carrying [BinaryTransfer] values arrives in: raw binary parts,
// each before the JSON that references it by index. Parts are read as they arrive, a section at a
// time, so a stream holds no more than the section it is in. The boundary is random per response, and
// a part's content is never scanned for anything but the delimiter, as the server's writer assumes.

import { ScryBinary } from "../wire.ts";
import { ScryWireError } from "../errors.ts";

export interface Section {
  /** The section's media type, lower-cased, without its parameters. */
  readonly type: string;
  readonly body: Uint8Array;
}

/** The boundary a multipart content type names, without the quotes it may be written in. */
export function boundaryOf(contentType: string): string {
  const parameter = /;\s*boundary=("([^"]*)"|[^;\s]+)/i.exec(contentType);
  const boundary = parameter?.[2] ?? parameter?.[1];
  if (boundary === undefined ||
      boundary.length === 0) {
    throw new ScryWireError("A multipart response arrived without a boundary.");
  }

  return boundary;
}

const crlf = [0x0d, 0x0a];

/** Each section of a multipart body as it is read whole, ending at the closing delimiter. */
export async function* sections(body: ReadableStream<Uint8Array>, boundary: string, signal?: AbortSignal): AsyncIterable<Section> {
  const encoder = new TextEncoder();
  const opening = encoder.encode(`--${boundary}`);
  const delimiter = encoder.encode(`\r\n--${boundary}`);
  const buffer = new Bytes(body.getReader(), signal);
  try {
    // Anything before the first delimiter is a preamble, which says nothing.
    let at = await buffer.find(opening, 0);
    if (at < 0) {
      throw new ScryWireError("A multipart response ended before its first part.");
    }

    let position = at + opening.length;
    while (true) {
      if (await buffer.startsWith([0x2d, 0x2d], position)) {
        return;
      }

      // A delimiter line may carry trailing white space before its line break. A part with no headers
      // has its blank line straight after, so the search for their end starts on that line break.
      position = await buffer.lineEnd(position);
      const headersEnd = await buffer.find(new Uint8Array([...crlf, ...crlf]), position - 2);
      if (headersEnd < 0) {
        throw new ScryWireError("A multipart response ended inside a part's headers.");
      }

      const headers = new TextDecoder().decode(buffer.slice(position, Math.max(position, headersEnd)));
      // The line break before a delimiter is the delimiter's, so an empty part's ends where it starts.
      const start = headersEnd + 4;
      at = await buffer.find(delimiter, start);
      if (at < 0) {
        throw new ScryWireError("A multipart response ended inside a part.");
      }

      const section = sectionOf(headers, buffer.slice(start, Math.max(start, at)));
      position = at + delimiter.length;
      buffer.discard(position);
      position = 0;
      yield section;
    }
  } finally {
    buffer.release();
  }
}

function sectionOf(headers: string, body: Uint8Array): Section {
  let type = "";
  let length: string | undefined;
  for (const line of headers.split("\r\n")) {
    const colon = line.indexOf(":");
    if (colon < 0) {
      continue;
    }

    const name = line.slice(0, colon).trim().toLowerCase();
    const value = line.slice(colon + 1).trim();
    if (name === "content-type") {
      type = value.split(";")[0]!.trim().toLowerCase();
    } else if (name === "content-length") {
      length = value;
    }
  }

  // Advisory, and never trusted for the read: a part whose length disagrees is a malformed response
  // rather than one to pad or cut.
  if (length !== undefined &&
      Number(length) !== body.length) {
    throw new ScryWireError(`A multipart part declared ${length} bytes and carried ${body.length}.`);
  }

  return { type, body };
}

/** Whether a section is a raw binary part, rather than the JSON that references it. */
export function isBinary(section: Section): boolean {
  return section.type === ScryBinary.PartContentType;
}

/** A single or batch body: the binary parts in wire order, then the JSON envelope, which is last. */
export async function readMultipart(body: ReadableStream<Uint8Array>, boundary: string): Promise<{ readonly envelope: string; readonly parts: Uint8Array[] }> {
  const parts: Uint8Array[] = [];
  let envelope: string | undefined;
  for await (const section of sections(body, boundary)) {
    if (envelope !== undefined) {
      throw new ScryWireError("A multipart response continued past its JSON part.");
    }

    if (isBinary(section)) {
      parts.push(section.body);
      continue;
    }

    envelope = new TextDecoder().decode(section.body);
  }

  if (envelope === undefined) {
    throw new ScryWireError("A multipart response arrived without a JSON part.");
  }

  return { envelope, parts };
}

// The bytes read so far, read further as a search needs them.
class Bytes {
  readonly #reader: ReadableStreamDefaultReader<Uint8Array>;
  readonly #signal: AbortSignal | undefined;
  #bytes = new Uint8Array(0);
  #length = 0;
  #done = false;

  constructor(reader: ReadableStreamDefaultReader<Uint8Array>, signal: AbortSignal | undefined) {
    this.#reader = reader;
    this.#signal = signal;
  }

  /** Where the needle starts, at or after `from`, or -1 where the body ends first. */
  async find(needle: Uint8Array, from: number): Promise<number> {
    let start = Math.max(0, from);
    while (true) {
      const found = indexOf(this.#bytes, this.#length, needle, start);
      if (found >= 0) {
        return found;
      }

      start = Math.max(start, this.#length - needle.length + 1);
      if (!await this.#more()) {
        return -1;
      }
    }
  }

  async startsWith(prefix: readonly number[], at: number): Promise<boolean> {
    while (this.#length < at + prefix.length) {
      if (!await this.#more()) {
        return false;
      }
    }

    return prefix.every((_, index) => this.#bytes[at + index] === _);
  }

  /** Past the line break of the line `from` is on. */
  async lineEnd(from: number): Promise<number> {
    const end = await this.find(new Uint8Array(crlf), from);
    if (end < 0) {
      throw new ScryWireError("A multipart response ended inside a delimiter line.");
    }

    return end + 2;
  }

  slice(start: number, end: number): Uint8Array {
    return this.#bytes.slice(start, end);
  }

  /** Forgets everything before `count`, which has been read. */
  discard(count: number): void {
    this.#bytes.copyWithin(0, count, this.#length);
    this.#length -= count;
  }

  release(): void {
    this.#reader.releaseLock();
  }

  async #more(): Promise<boolean> {
    if (this.#done ||
        this.#signal?.aborted === true) {
      return false;
    }

    const { done, value } = await this.#reader.read();
    if (done) {
      this.#done = true;
      return false;
    }

    if (this.#length + value.length > this.#bytes.length) {
      const grown = new Uint8Array(Math.max(this.#bytes.length * 2, this.#length + value.length, 4096));
      grown.set(this.#bytes.subarray(0, this.#length));
      this.#bytes = grown;
    }

    this.#bytes.set(value, this.#length);
    this.#length += value.length;
    return true;
  }
}

function indexOf(haystack: Uint8Array, length: number, needle: Uint8Array, from: number): number {
  const first = needle[0]!;
  const last = length - needle.length;
  for (let index = from; index <= last; index++) {
    if (haystack[index] !== first) {
      continue;
    }

    let matched = 1;
    while (matched < needle.length &&
           haystack[index + matched] === needle[matched]) {
      matched++;
    }

    if (matched === needle.length) {
      return index;
    }
  }

  return -1;
}
