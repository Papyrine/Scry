// Reading a server's description, and writing what is generated from it. Shared by the command line
// and the Vite plugin, so both read a description the same way and leave an unchanged file alone.

import { mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import { basename, dirname, join } from "node:path";
import type { ScryIntrospection } from "../wire.ts";

/** Whether the input names a running server's introspection rather than a file. */
export function isUrl(input: string): boolean {
  return /^https?:\/\//i.test(input);
}

/** Reads a description from a file, or from a URL. */
export async function readDescription(input: string): Promise<ScryIntrospection> {
  let text: string;
  if (isUrl(input)) {
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

/**
 * Writes the generated code where it differs from what is there, whole and then moved into place, so
 * a watcher on the file is told only of a change, and never reads half of one. True where it wrote.
 */
export async function writeGenerated(path: string, code: string): Promise<boolean> {
  let existing: string | undefined;
  try {
    existing = await readFile(path, "utf8");
  } catch {
    // Not there yet.
  }

  if (existing === code) {
    return false;
  }

  const directory = dirname(path);
  await mkdir(directory, { recursive: true });
  const writing = join(directory, `.${basename(path)}.${process.pid}.${Date.now()}.tmp`);
  try {
    await writeFile(writing, code);
    await rename(writing, path);
  } finally {
    await rm(writing, { force: true });
  }

  return true;
}
