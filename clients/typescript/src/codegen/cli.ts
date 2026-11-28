#!/usr/bin/env node
// scry-codegen: writes the TypeScript a client queries with from a server's introspection.

import { parseArgs } from "node:util";
import { readDescription, writeGenerated } from "./description.ts";
import { generate } from "./generate.ts";

export { readDescription } from "./description.ts";

const usage = `Usage: scry-codegen --input <file or URL> --output <file> [--runtime <module>]

  --input    The server's description: the file a server writes with WriteScryDescription, or
             the URL the explorer reads it from ({explorer route}/introspect, mapped in
             Development by default).
  --output   Where to write the generated TypeScript.
  --runtime  The module the generated code imports the client's types from. Default: scry-client.`;

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
  if (!await writeGenerated(values.output, code)) {
    console.log(`${values.output} is already what ${values.input} generates (schema stamp ${description.schemaStamp}).`);
    return 0;
  }

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
