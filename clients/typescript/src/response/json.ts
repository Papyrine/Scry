// Reads a response as JSON, keeping every number as the text it was written as. A long past 2^53 and
// a decimal of twenty-nine digits would lose digits as a JavaScript number, so a number is read as one
// only once the decode plan says what the member is.

import { ScryWireError } from "../errors.ts";

/** A JSON number, as the text the server wrote. */
export class RawNumber {
  readonly text: string;

  constructor(text: string) {
    this.text = text;
  }
}

export type Json = null | boolean | string | RawNumber | Json[] | { [key: string]: Json };

const escapes: Record<string, string> = {
  "\"": "\"",
  "/": "/",
  b: String.fromCharCode(8),
  f: String.fromCharCode(12),
  n: String.fromCharCode(10),
  r: String.fromCharCode(13),
  t: String.fromCharCode(9)
};
escapes[String.fromCharCode(92)] = String.fromCharCode(92);

const backslash = 92;
const number = /-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/y;

/** The JSON a response holds, or a failure saying where it stopped being JSON. */
export function parseJson(text: string): Json {
  const reader = new Reader(text);
  const value = reader.value();
  reader.space();
  if (reader.index !== text.length) {
    throw reader.fail("after the end of the value");
  }

  return value;
}

class Reader {
  readonly text: string;
  index = 0;

  constructor(text: string) {
    this.text = text;
  }

  fail(where: string): ScryWireError {
    if (this.index >= this.text.length) {
      return new ScryWireError("The response ended early: it is not complete JSON, which is how a body cut off part way reads.");
    }

    return new ScryWireError(`The response is not JSON: unexpected '${this.text.charAt(this.index)}' at ${this.index}, ${where}.`);
  }

  space(): void {
    while (this.index < this.text.length) {
      const code = this.text.charCodeAt(this.index);
      if (code !== 0x20 &&
          code !== 0x09 &&
          code !== 0x0a &&
          code !== 0x0d) {
        return;
      }

      this.index++;
    }
  }

  value(): Json {
    this.space();
    const next = this.text.charAt(this.index);
    switch (next) {
      case "{":
        return this.object();
      case "[":
        return this.array();
      case "\"":
        return this.string();
      case "t":
        return this.word("true", true);
      case "f":
        return this.word("false", false);
      case "n":
        return this.word("null", null);
      default:
        return this.number();
    }
  }

  word<T extends Json>(word: string, value: T): T {
    if (!this.text.startsWith(word, this.index)) {
      throw this.fail("where a value starts");
    }

    this.index += word.length;
    return value;
  }

  number(): RawNumber {
    number.lastIndex = this.index;
    const match = number.exec(this.text);
    if (match === null) {
      throw this.fail("where a value starts");
    }

    this.index += match[0].length;
    return new RawNumber(match[0]);
  }

  string(): string {
    this.index++;
    let out = "";
    let start = this.index;
    while (true) {
      if (this.index >= this.text.length) {
        throw this.fail("inside a string");
      }

      const code = this.text.charCodeAt(this.index);
      if (code === 0x22) {
        out += this.text.slice(start, this.index);
        this.index++;
        return out;
      }

      if (code < 0x20) {
        throw this.fail("inside a string, where a control character has to be escaped");
      }

      if (code !== backslash) {
        this.index++;
        continue;
      }

      out += this.text.slice(start, this.index);
      const escape = this.text.charAt(this.index + 1);
      if (escape === "u") {
        const hex = this.text.slice(this.index + 2, this.index + 6);
        if (!/^[0-9a-fA-F]{4}$/.test(hex)) {
          this.index += 2;
          throw this.fail("in a unicode escape");
        }

        out += String.fromCharCode(Number.parseInt(hex, 16));
        this.index += 6;
      } else {
        const unescaped = escapes[escape];
        if (unescaped === undefined) {
          this.index++;
          throw this.fail("in an escape");
        }

        out += unescaped;
        this.index += 2;
      }

      start = this.index;
    }
  }

  array(): Json[] {
    this.index++;
    const items: Json[] = [];
    this.space();
    if (this.text.charAt(this.index) === "]") {
      this.index++;
      return items;
    }

    while (true) {
      items.push(this.value());
      this.space();
      const next = this.text.charAt(this.index);
      this.index++;
      if (next === "]") {
        return items;
      }

      if (next !== ",") {
        this.index--;
        throw this.fail("between the items of an array");
      }
    }
  }

  object(): { [key: string]: Json } {
    this.index++;
    const members: { [key: string]: Json } = {};
    this.space();
    if (this.text.charAt(this.index) === "}") {
      this.index++;
      return members;
    }

    while (true) {
      this.space();
      if (this.text.charAt(this.index) !== "\"") {
        throw this.fail("where a member's name starts");
      }

      const key = this.string();
      this.space();
      if (this.text.charAt(this.index) !== ":") {
        throw this.fail("after a member's name");
      }

      this.index++;
      Object.defineProperty(members, key, { value: this.value(), enumerable: true, writable: true, configurable: true });
      this.space();
      const next = this.text.charAt(this.index);
      this.index++;
      if (next === "}") {
        return members;
      }

      if (next !== ",") {
        this.index--;
        throw this.fail("between the members of an object");
      }
    }
  }
}
