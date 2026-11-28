// Commands over HTTP: what is sent, read against what the C# client sends, and what becomes of each
// answer the server gives, read from the exchanges the .NET side recorded.

import { test } from "node:test";
import assert from "node:assert/strict";
import {
  createCommands,
  createScry,
  httpTransport,
  resultOf,
  ScryCommandFailedError,
  ScryNotServedError,
  ScryPermissionError,
  ScryRequestError
} from "../src/index.ts";
import type { CommandsOptions } from "../src/index.ts";
import { writePayload } from "../src/commands/payload.ts";
import { Scope } from "../src/query/expression.ts";
import { schema, type Commands, type Sources } from "./generated/test-model.ts";
import recorded from "../../../src/Scry.Tests/TypeScript/commands.verified.json" with { type: "json" };

const endpoint = "https://scry.test/api/query";
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

function exchange(name: string) {
  const found = recorded.find(_ => _.name === name);
  if (found === undefined) {
    throw new Error(`No exchange is recorded as '${name}'.`);
  }

  return found;
}

function answer(name: string): Response {
  const recording = exchange(name);
  return new Response(recording.body, { status: recording.status, headers: recording.headers });
}

const pendingEvent = exchange("A command going pending, then its outcome").body.split("\n\n")[0] + "\n\n";
const outcomeEvent = exchange("A command going pending, then its outcome").body.split("\n\n")[1] + "\n\n";
const events = { "Content-Type": "text/event-stream" };

// An event stream that sends the pending receipt, then whatever `rest` settles with: more of the
// stream, or nothing, which cuts it there.
function held(rest: Promise<string | undefined>): Response {
  const encoder = new TextEncoder();
  const body = new ReadableStream<Uint8Array>({
    async start(controller) {
      controller.enqueue(encoder.encode(pendingEvent));
      const more = await rest;
      if (more !== undefined) {
        controller.enqueue(encoder.encode(more));
      }

      controller.close();
    }
  });
  return new Response(body, { headers: events });
}

interface Seen {
  readonly url: string;
  readonly method: string;
  readonly headers: Record<string, string>;
  readonly body: string | undefined;
}

function client(respond: (seen: Seen, index: number) => Response | Promise<Response>, options: CommandsOptions = {}) {
  const seen: Seen[] = [];
  const fetch = async (input: string | URL | Request, init?: RequestInit) => {
    const request: Seen = {
      url: String(input),
      method: init?.method ?? "GET",
      headers: { ...(init?.headers as Record<string, string> | undefined) },
      body: init?.body as string | undefined
    };
    seen.push(request);
    return respond(request, seen.length - 1);
  };
  const scry = createScry<Sources>(schema, { stamp: false, transport: httpTransport({ endpoint, fetch }) });
  const commands = createCommands<Commands>(scry, { retry: () => 0, ...options });
  return { commands, seen };
}

// The id is the one thing a client makes up, so a recorded request is compared with its id put back.
function withId(body: string | undefined, id: number): string {
  return (body ?? "").replace(/"id":"[0-9a-f-]{36}"/, `"id":"00000000-0000-0000-0000-${String(id).padStart(12, "0")}"`);
}

const shift = { day: "2026-03-04", name: `Late <shift> & "night" ${String.fromCharCode(0xe9)}`, perks: [["Parking", "Gym"] as const, "None" as const] };

test("a command is sent as the C# client sends it, its payload as the server binds it", async () => {
  const { commands, seen } = client(() => answer("A command answered at once, with its result"));

  await commands.send.createShift(shift);

  const sent = seen[0]!;
  assert.equal(sent.url, `${endpoint}/command`);
  assert.equal(sent.method, "POST");
  assert.equal(sent.headers["Accept"], "application/json, text/event-stream");
  assert.equal(withId(sent.body, 1), exchange("A command answered at once, with its result").requestBody);
});

test("a command decided at once is its outcome, its result read as the model says", async () => {
  const { commands } = client(() => answer("A command answered at once, with its result"));

  const outcome = await commands.send.createShift(shift);

  assert.equal(outcome.status, "Completed");
  assert.equal(outcome.command, "CreateShift");
  assert.match(outcome.id, uuid);
  assert.deepEqual(resultOf(outcome), { id: 7 });
  assert.equal(await outcome.completion, outcome);
  assert.deepEqual(commands.pending, []);
});

test("a command the handler failed is an outcome carrying the reason the server showed", async () => {
  const { commands } = client(() => answer("A command that failed"));

  const outcome = await commands.send.renameShift({ id: 1, name: "Night" });

  assert.equal(outcome.status, "Failed");
  assert.equal(outcome.status === "Failed" && outcome.error, "The rota is locked this week.");
  assert.throws(() => resultOf(outcome), (error: unknown) => error instanceof ScryCommandFailedError && error.outcome === outcome);
});

test("a command the server would not accept throws, as a refused query does", async () => {
  const { commands } = client(() => answer("A command refused"));

  await assert.rejects(
    () => commands.send.createShift(shift),
    (error: unknown) => error instanceof ScryRequestError && error.code === "Validation" && error.status === 400 && /colour/.test(error.message));
});

test("a target gone by the time the command arrived is a race lost, not a refusal", async () => {
  const { commands } = client(() => answer("A command whose target is not there"));

  const outcome = await commands.send.sealContract({ contractId: 999 });

  assert.deepEqual({ status: outcome.status, error: outcome.status === "Failed" && outcome.error }, { status: "Failed", error: "The command's target was not found." });
});

test("a command answered pending and then decided within the wait is its outcome", async () => {
  const { commands } = client(() => answer("A command going pending, then its outcome"));

  const outcome = await commands.send.renameShift({ id: 1, name: "Slow" });

  assert.equal(outcome.status, "Completed");
  assert.deepEqual(commands.pending, []);
});

test("one still running when the wait runs out is pending, listed until its outcome lands", async () => {
  let release!: (more: string | undefined) => void;
  const { commands } = client(() => held(new Promise(resolve => release = resolve)), { wait: 0, completedLinger: 0 });
  let told = 0;
  commands.onPendingChanged(() => told++);

  const outcome = await commands.send.renameShift({ id: 1, name: "Slow" });

  assert.equal(outcome.status, "Pending");
  assert.deepEqual(commands.pending.map(_ => ({ command: _.command, target: _.target, keys: _.keys, status: _.status })), [{ command: "RenameShift", target: "Shift", keys: [1], status: "Pending" }]);

  release(outcomeEvent);
  const final = await outcome.completion;

  assert.equal(final.status, "Completed");
  assert.equal(final.id, outcome.id);
  assert.deepEqual(commands.pending, []);
  assert.equal(told, 3);
});

test("one whose connection is cut is asked for again by its id", async () => {
  const { commands, seen } = client((_, index) => {
    if (index === 0) {
      return held(Promise.resolve(undefined));
    }

    return answer("A command asked for again by its id");
  }, { wait: Infinity });

  const outcome = await commands.send.renameShift({ id: 1, name: "Slow" });

  assert.equal(outcome.status, "Completed");
  assert.equal(seen[1]!.method, "GET");
  assert.equal(seen[1]!.url, `${endpoint}/command/${outcome.id}`);
});

test("one whose stream fails for the moment is asked for again", async () => {
  const failure = `event: error\ndata: {"error":"Command dispatch failed.","code":"ExecutionFailed"}\n\n`;
  const { commands, seen } = client((_, index) => {
    if (index === 0) {
      return held(Promise.resolve(failure));
    }

    return answer("A command asked for again by its id");
  }, { wait: Infinity });

  const outcome = await commands.send.renameShift({ id: 1, name: "Slow" });

  assert.equal(outcome.status, "Completed");
  assert.equal(seen.length, 2);
});

test("one the server no longer holds when asked again is unknown, and stays listed until cleared", async () => {
  let release!: (more: string | undefined) => void;
  const { commands } = client((_, index) => {
    if (index === 0) {
      return held(new Promise(resolve => release = resolve));
    }

    return answer("An id nobody sent");
  }, { wait: 0 });

  const outcome = await commands.send.renameShift({ id: 1, name: "Slow" });
  release(undefined);
  const final = await outcome.completion;

  assert.equal(final.status, "Unknown");
  assert.match(final.status === "Unknown" ? final.error : "", /no longer holds/);
  assert.deepEqual(commands.pending.map(_ => _.status), ["Unknown"]);
  commands.clearFinished();
  assert.deepEqual(commands.pending, []);
});

test("one the server will not answer for when asked again is unknown, saying why", async () => {
  const { commands } = client((_, index) => {
    if (index === 0) {
      return held(Promise.resolve(undefined));
    }

    return new Response("<html>Sign in</html>", { status: 401, headers: { "Content-Type": "text/html" } });
  }, { wait: Infinity });

  const outcome = await commands.send.renameShift({ id: 1, name: "Slow" });

  assert.equal(outcome.status, "Unknown");
  assert.match(outcome.status === "Unknown" ? outcome.error : "", /\(answered 401\)\. It may have run\./);
});

test("closing stops following, and each command followed ends unknown", async () => {
  const { commands } = client(() => held(new Promise(() => {})), { wait: 0 });

  const outcome = await commands.send.renameShift({ id: 1, name: "Slow" });
  await commands.close();
  const final = await outcome.completion;

  assert.equal(final.status, "Unknown");
  await assert.rejects(() => commands.send.renameShift({ id: 1, name: "Again" }), /closed/);
});

test("a server not serving commands says so", async () => {
  const { commands } = client(() => new Response("", { status: 404 }));

  await assert.rejects(() => commands.send.renameShift({ id: 1, name: "Night" }), ScryNotServedError);
});

test("what a caller may send is false until the server has said, then what it said", async () => {
  const { commands, seen } = client(() => answer("What a caller may send"));
  let told = 0;
  commands.onCapabilitiesChanged(() => told++);

  assert.equal(commands.can("createShift"), false);
  await commands.ready;

  assert.equal(seen[0]!.url, `${endpoint}/capabilities`);
  assert.deepEqual([commands.can("createShift"), commands.can("renameShift"), commands.can("sealContract")], [true, true, true]);
  assert.equal(told, 1);
  await commands.refreshCapabilities();
  assert.equal(told, 1);
});

test("a server with commands off allows nothing", async () => {
  const { commands } = client(() => new Response("", { status: 404 }));

  await commands.ready;

  assert.equal(commands.can("createShift"), false);
});

test("a denial throws, and asks again what the caller may send", async () => {
  const { commands, seen } = client(request => {
    if (request.method === "POST") {
      return new Response(JSON.stringify({ error: "The command was denied by a server policy.", code: "Forbidden" }), { status: 403, headers: { "Content-Type": "application/json" } });
    }

    return answer("What a caller may send");
  });

  await assert.rejects(() => commands.send.sealContract({ contractId: 1 }), ScryPermissionError);
  await new Promise(resolve => setImmediate(resolve));

  assert.deepEqual(seen.map(_ => _.url), [`${endpoint}/command`, `${endpoint}/capabilities`]);
});

test("a payload carrying what the command does not have, or a null it cannot hold, is refused before it is sent", async () => {
  const { commands, seen } = client(() => answer("A command that failed"));

  await assert.rejects(() => commands.send.renameShift({ id: 1, name: "Night", colour: "red" } as never), /carries 'colour'/);
  await assert.rejects(() => commands.send.renameShift({ id: null, name: "Night" } as never), /'id'.*cannot be null/);
  assert.equal(seen.length, 0);
});

test("each value is written in the JSON type the server's binder reads it from", () => {
  const scope = new Scope(schema);
  const info = {
    name: "Every",
    payload: {
      count: { name: "Count", shape: { kind: "value", value: { scalar: "long" }, nullable: false } },
      price: { name: "Price", shape: { kind: "value", value: { scalar: "decimal" }, nullable: false } },
      ratio: { name: "Ratio", shape: { kind: "value", value: { scalar: "double" }, nullable: false } },
      day: { name: "Day", shape: { kind: "value", value: { enum: "DayOfWeek" }, nullable: false } },
      photo: { name: "Photo", shape: { kind: "value", value: { scalar: "bytes" }, nullable: false } },
      starts: { name: "Starts", shape: { kind: "value", value: { scalar: "DateOnly" }, nullable: true } },
      team: { name: "Team", shape: { kind: "value", value: { scalar: "Guid" }, nullable: false } },
      grade: { name: "Grade", shape: { kind: "value", value: { scalar: "char" }, nullable: false } },
      tags: { name: "Tags", shape: { kind: "values", value: { scalar: "string" }, nullable: false } }
    }
  } as const;

  const written = writePayload(info, {
    count: 9007199254740993n,
    price: "12.50",
    ratio: 0.1,
    day: "Monday",
    photo: new Uint8Array([1, 2, 3]),
    starts: null,
    team: "6F9619FF-8B86-D011-B42D-00C04FC964FF",
    grade: "A",
    tags: ["a", "b"]
  }, scope.enumInfo);

  assert.equal(written, "{\"count\":9007199254740993,\"price\":12.50,\"ratio\":0.1,\"day\":\"Monday\",\"photo\":\"AQID\",\"team\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\",\"grade\":\"A\",\"tags\":[\"a\",\"b\"]}");
});
