// Every shape the C# corpus records, written as a TypeScript query, must send the same bytes. The
// corpus is WireShapeTests' on the .NET side, which guards that every operator, node and projected
// value is in it; so a shape added there fails here until it is written here too.

import { test } from "node:test";
import assert from "node:assert/strict";
import { createScry, serializeRequest } from "../src/index.ts";
import { apart, corpusCases } from "./corpus-cases.ts";
import type { QueryRequest } from "../src/wire.ts";
import { schema, type Sources } from "./generated/test-model.ts";
import corpus from "../../../src/Scry.Tests/TypeScript/requests.verified.json" with { type: "json" };

const stop = new Error("Stopped before sending.");
let sent: QueryRequest | undefined;

// As the corpus's C# sources were built: by hand, so a query projecting nothing leaves it to the server,
// and with no schema stamp.
const scry = createScry<Sources>(schema, {
  defaultProjection: "server",
  stamp: false,
  transport: {
    query(request) {
      sent = request;
      return Promise.reject(stop);
    }
  }
});

async function capture(run: () => Promise<unknown>): Promise<QueryRequest> {
  sent = undefined;
  try {
    await run();
  } catch (error) {
    if (error !== stop) {
      throw error;
    }
  }

  if (sent === undefined) {
    throw new Error("The query sent nothing.");
  }

  return sent;
}

const cases = corpusCases(scry);

for (const shape of corpus) {
  test(shape.csharp, async () => {
    if (Object.hasOwn(apart, shape.csharp)) {
      return;
    }

    const run = cases[shape.csharp];
    if (run === undefined) {
      assert.fail("No TypeScript query is written for this shape. Add one to test/corpus.test.ts.");
    }

    assert.equal(serializeRequest(await capture(run)), shape.request);
  });
}

test("every case is a shape the corpus has", () => {
  const shapes = new Set(corpus.map(_ => _.csharp));
  const stale = [...Object.keys(cases), ...Object.keys(apart)].filter(_ => !shapes.has(_));
  assert.deepEqual(stale, []);
});
