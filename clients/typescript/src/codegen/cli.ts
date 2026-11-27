#!/usr/bin/env node
// scry-codegen: writes the TypeScript a client queries with from a server's introspection.

import { readFile, writeFile } from "node:fs/promises";
import { parseArgs } from "node:util";
import { generate } from "./generate.ts";
import type { ScryIntrospection } from "../wire.ts";

const usage = `Usage: scry-codegen --input <file or URL> --output <file> [--runtime <module>]

  --input    The server's introspection: a JSON file, or the URL the explorer reads it from
             ({explorer route}/introspect, mapped in Development by default).
  --output   Where to write the generated TypeScript.
  --runtime  The module the generated code imports the client's types from. Default: scry-client.`;

/** Reads a description from a file, or from a URL. */
export async function readDescription(input: string): Promise<ScryIntrospection> {
  let text: string;
  if (/^https?:\/\//i.test(input)) {
    const response = await fetch(input);
    if (!response.ok) {
      throw new Error(`${input} answered ${response.status} ${response.statusText}.`);
    }

    text = await response.text();
  } else {
    text = await readFile(input, "utf8");
  }

  // A description saved by a .NET tool may start with a byte-order mark, which JSON does not allow.
  if (text.charCodeAt(0) === 0xfeff) {
    text = text.slice(1);
  }

  return JSON.parse(text) as ScryIntrospection;
}

async function main(args: string[]): Promise<number> {
  const { values } = parseArgs({
    args,
    options: {
      input: { type: "string" },
      output: { type: "string" },
      runtime: { type: "string", default: "scry-client" },
      help: { type: "boolean", short: "h" }
    }
  });

  if (values.help === true) {
    console.log(usage);
    return 0;
  }

  if (values.input === undefined ||
      values.output === undefined) {
    console.error(usage);
    return 2;
  }

  const description = await readDescription(values.input);
  const code = generate(description, { runtime: values.runtime, from: values.input });
  await writeFile(values.output, code);
  console.log(`Wrote ${values.output} from ${values.input} (schema stamp ${description.schemaStamp}).`);
  return 0;
}

if (import.meta.main) {
  try {
    process.exitCode = await main(process.argv.slice(2));
  } catch (error) {
    console.error((error as Error).message);
    process.exitCode = 1;
  }
}
