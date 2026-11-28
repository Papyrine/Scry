// Writes the generated code the tests compare against. Run after a change to the generator, or after
// the .NET side accepts a new description, and review the difference as the change it makes.

import { writeFile, mkdir } from "node:fs/promises";
import { dirname } from "node:path";
import { readDescription } from "../src/codegen/cli.ts";
import { generate } from "../src/codegen/generate.ts";
import { descriptionPath, fixtures, runtime } from "../test/fixtures.ts";

for (const fixture of fixtures) {
  const description = await readDescription(descriptionPath(fixture));
  await mkdir(dirname(fixture.generated), { recursive: true });
  await writeFile(fixture.generated, generate(description, { runtime, from: fixture.description }));
  console.log(`Wrote ${fixture.generated}`);
}
