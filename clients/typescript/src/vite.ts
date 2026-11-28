// A Vite plugin generating a client's model from a server's description: as a build or the dev server
// starts, and again whenever the description file changes, which a server writes on startup with
// WriteScryDescription. The TypeScript counterpart of the C# client's build, which generates from the
// model DLL on every compile, and optional: the package's own entry point imports nothing of Vite, and
// an app without it runs scry-codegen instead. The generated file is a real one, not a virtual module,
// since the type-checker and the editor read files.

import { access } from "node:fs/promises";
import { resolve } from "node:path";
import type { Logger, Plugin } from "vite";
import { isUrl, readDescription, writeGenerated } from "./codegen/description.ts";
import { generate } from "./codegen/generate.ts";

export interface ScryPluginOptions {
  /**
   * The description: the file a server writes with WriteScryDescription, relative to Vite's root, or
   * the URL of a running server's introspection, which is read once as the build starts.
   */
  readonly input: string;
  /** Where to write the generated TypeScript, relative to Vite's root. */
  readonly output: string;
  /** The module the generated code imports the client's types from. Defaults to `scry-client`. */
  readonly runtime?: string;
}

/** Generates the model from the description, and keeps it current while the description changes. */
export function scry(options: ScryPluginOptions): Plugin {
  let root = process.cwd();
  let logger: Logger | undefined;
  const remote = isUrl(options.input);
  const input = () => {
    if (remote) {
      return options.input;
    }

    return resolve(root, options.input);
  };
  const output = () => resolve(root, options.output);

  // True where the generated file changed. A description that cannot be read keeps the file as it
  // was, where there is one: a build on a machine that never started the server still builds.
  async function run(): Promise<boolean> {
    let description;
    try {
      description = await readDescription(input());
    } catch (error) {
      if (await exists(output())) {
        logger?.warn(`[scry] ${options.input} could not be read (${(error as Error).message}), so ${options.output} is kept as it was.`);
        return false;
      }

      throw new Error(
        `[scry] ${options.input} could not be read, and there is no ${options.output} to build with: start the server, which writes it, or point input at its introspection.`,
        { cause: error });
    }

    return writeGenerated(output(), generate(description, { runtime: options.runtime ?? "scry-client", from: options.input }));
  }

  return {
    name: "scry",

    configResolved(config) {
      root = config.root;
      logger = config.logger;
    },

    async buildStart() {
      if (await run()) {
        logger?.info(`[scry] generated ${options.output} from ${options.input}`);
      }

      if (!remote) {
        this.addWatchFile(input());
      }
    },

    configureServer(server) {
      if (remote) {
        return;
      }

      server.watcher.add(input());
      const changed = async (path: string) => {
        if (!samePath(path, input())) {
          return;
        }

        try {
          if (await run()) {
            logger?.info(`[scry] generated ${options.output} from ${options.input}`);
          }
        } catch (error) {
          logger?.error((error as Error).message);
        }
      };
      server.watcher.on("add", changed);
      server.watcher.on("change", changed);
    }
  };
}

async function exists(path: string): Promise<boolean> {
  try {
    await access(path);
    return true;
  } catch {
    return false;
  }
}

// A watcher names a path as the platform spells it, which on Windows is in either case.
function samePath(left: string, right: string): boolean {
  const a = resolve(left);
  const b = resolve(right);
  if (process.platform === "win32") {
    return a.toLowerCase() === b.toLowerCase();
  }

  return a === b;
}
