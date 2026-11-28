// The descriptions code is generated from in the tests, both kept by the .NET side as snapshots: the
// test model the request corpus is written against, and the sample model as the explorer endpoint
// serves it. What is generated from each is committed beside the tests, so a change to the generator
// is reviewed as a change to its output.

import { join } from "node:path";

const root = join(import.meta.dirname, "..", "..", "..");

export interface Fixture {
  readonly name: string;
  /** The description, as a path from the repository root. */
  readonly description: string;
  /** Where its generated code is kept. */
  readonly generated: string;
}

export const fixtures: readonly Fixture[] = [
  {
    name: "the test model",
    description: "src/Scry.Tests/TypeScript/introspection.verified.json",
    generated: join(import.meta.dirname, "generated", "test-model.ts")
  },
  {
    name: "the sample model",
    description: "samples/Sample.Tests/UiSnapshotTests.ExplorerIntrospectionEndpoint.verified.txt",
    generated: join(import.meta.dirname, "generated", "sample-model.ts")
  }
];

/** The runtime, as generated code in test/generated imports it. */
export const runtime = "../../src/runtime/index.ts";

export function descriptionPath(fixture: Fixture): string {
  return join(root, fixture.description);
}
