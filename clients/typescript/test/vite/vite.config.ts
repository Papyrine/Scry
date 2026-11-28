// The Vite configuration docs/typescript.md shows, which test/vite.test.ts builds and serves with,
// over a root of its own holding the description and a module importing what is generated. An app
// imports the plugin as "scry-client/vite".

import { defineConfig } from "vite";
import { scry } from "../../src/vite.ts";

// begin-snippet: typescriptVitePlugin
export default defineConfig({
  plugins: [
    scry({ input: "scry.description.json", output: "src/scry.generated.ts" })
  ]
});
// end-snippet
