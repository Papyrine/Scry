// The Vite plugin, driven by Vite itself: a build that generates the model before it bundles what
// imports it, a dev server that generates it again when the description changes, and the answers
// for a description that is not there yet.

import { test } from "node:test";
import assert from "node:assert/strict";
import { copyFile, mkdir, mkdtemp, readFile, rm, stat, utimes, writeFile } from "node:fs/promises";
import { createServer as createHttpServer, type Server } from "node:http";
import type { AddressInfo } from "node:net";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { build, createServer, type InlineConfig } from "vite";
import { readDescription } from "../src/codegen/description.ts";
import { generate } from "../src/codegen/generate.ts";
import { scry } from "../src/vite.ts";
import { descriptionPath, fixtures } from "./fixtures.ts";

const configFile = join(import.meta.dirname, "vite", "vite.config.ts");
const description = descriptionPath(fixtures[0]!);

// A root of the app's own: the description the server wrote, and a module reading what is generated.
async function app(withDescription = true): Promise<string> {
  const root = await mkdtemp(join(tmpdir(), "scry-vite-"));
  await mkdir(join(root, "src"));
  if (withDescription) {
    await copyFile(description, join(root, "scry.description.json"));
  }

  await writeFile(join(root, "src", "index.ts"), "import { schema } from \"./scry.generated.ts\";\nexport const stamp: string = schema.stamp;\n");
  return root;
}

async function bundle(root: string, config: InlineConfig = { configFile }): Promise<string> {
  const output = await build({
    ...config,
    root,
    logLevel: "silent",
    build: { write: false, lib: { entry: join(root, "src", "index.ts"), formats: ["es"], fileName: "index" } }
  });
  let code = "";
  for (const result of [output].flat()) {
    if (!("output" in result)) {
      continue;
    }

    for (const chunk of result.output) {
      if ("code" in chunk) {
        code += chunk.code;
      }
    }
  }

  return code;
}

const generated = (root: string) => join(root, "src", "scry.generated.ts");

async function until(holds: () => boolean | Promise<boolean>, what: string): Promise<void> {
  const deadline = Date.now() + 15_000;
  while (!await holds()) {
    if (Date.now() > deadline) {
      assert.fail(`Timed out waiting for ${what}.`);
    }

    await new Promise(resolve => setTimeout(resolve, 50));
  }
}

test("a build generates the model from the description before it bundles what imports it", async () => {
  const root = await app();
  try {
    const code = await bundle(root);
    const read = await readDescription(description);
    const expected = generate(read, { runtime: "scry-client", from: "scry.description.json" });

    assert.equal(await readFile(generated(root), "utf8"), expected);
    assert.ok(code.includes(read.schemaStamp), "The bundle holds the stamp the generated model carries.");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("a model already what the description generates is left alone", async () => {
  const root = await app();
  try {
    await bundle(root);
    const before = new Date("2000-01-01T00:00:00Z");
    await utimes(generated(root), before, before);

    await bundle(root);

    assert.equal((await stat(generated(root))).mtime.getTime(), before.getTime());
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("with no description yet, a build keeps the model it has, and fails only where it has none", async () => {
  const root = await app(false);
  try {
    await assert.rejects(() => bundle(root), /could not be read, and there is no src\/scry\.generated\.ts to build with/);

    await writeFile(generated(root), "export const schema = { stamp: \"kept\" };\n");
    await bundle(root);

    assert.equal(await readFile(generated(root), "utf8"), "export const schema = { stamp: \"kept\" };\n");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});

test("the dev server generates the model again when the server writes a new description", async () => {
  const root = await app();
  const server = await createServer({ configFile, root, logLevel: "silent", server: { middlewareMode: true, hmr: false } });
  try {
    await server.pluginContainer.buildStart({});
    const { schemaStamp } = await readDescription(description);
    assert.ok((await readFile(generated(root), "utf8")).includes(`Schema stamp: ${schemaStamp}`));

    // A change made before the watcher has finished its first look at the root is one it never sees,
    // which a dev server running for longer than a test does not meet.
    await until(() => Object.values(server.watcher.getWatched()).some(_ => _.includes("scry.description.json")), "the watcher to see the description");

    const changed = (await readFile(description, "utf8")).replace(/"schemaStamp":\s*"[^"]*"/, "\"schemaStamp\":\"moved\"");
    await writeFile(join(root, "scry.description.json"), changed);

    await until(async () => (await readFile(generated(root), "utf8")).includes("Schema stamp: moved"), "the model to be generated again");
  } finally {
    await server.close();
    await rm(root, { recursive: true, force: true });
  }
});

// A running server's introspection, as the endpoint serves it.
async function serving(body: string): Promise<{ readonly url: string; readonly server: Server }> {
  const server = createHttpServer((request, response) => {
    if (request.url !== "/scry/introspect") {
      response.writeHead(404).end();
      return;
    }

    response.writeHead(200, { "Content-Type": "application/json" }).end(body);
  });
  await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
  const { port } = server.address() as AddressInfo;
  return { url: `http://127.0.0.1:${port}/scry/introspect`, server };
}

test("a build generates the model from a running server's introspection, read once as it starts", async () => {
  const root = await app(false);
  const { url, server } = await serving(await readFile(description, "utf8"));
  try {
    const code = await bundle(root, { configFile: false, plugins: [scry({ input: url, output: "src/scry.generated.ts" })] });
    const read = await readDescription(description);

    assert.equal(await readFile(generated(root), "utf8"), generate(read, { runtime: "scry-client", from: url }));
    assert.ok(code.includes(read.schemaStamp), "The bundle holds the stamp the generated model carries.");
  } finally {
    server.close();
    await rm(root, { recursive: true, force: true });
  }
});

test("a server that does not answer keeps the model a build has, and fails one with none", async () => {
  const root = await app(false);
  const { url, server } = await serving(await readFile(description, "utf8"));
  await new Promise(resolve => server.close(resolve));
  const config: InlineConfig = { configFile: false, plugins: [scry({ input: url, output: "src/scry.generated.ts" })] };
  try {
    await assert.rejects(() => bundle(root, config), /could not be read, and there is no src\/scry\.generated\.ts to build with/);

    await writeFile(generated(root), "export const schema = { stamp: \"kept\" };\n");
    await bundle(root, config);

    assert.equal(await readFile(generated(root), "utf8"), "export const schema = { stamp: \"kept\" };\n");
  } finally {
    await rm(root, { recursive: true, force: true });
  }
});
