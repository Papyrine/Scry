// A console client for Sample.WebServer in TypeScript, writing the queries Sample.ConsoleClient writes
// in C#, against a model generated from the server's own description (npm run generate).
//
//   node src/main.ts                  the tables, then exit
//   node src/main.ts --live           watch the orders until Ctrl+C
//   node src/main.ts --live --loop    the same, read with for await rather than handed to a callback
//   node src/main.ts --pages          every employee, two at a time: by offset, then by cursor
//   node src/main.ts --reprice <id>   send the RepriceOrder command, and print its outcome: the write to
//                                     make in one terminal while another runs --live
//   node src/main.ts --photos         open each employee's photo, an attachment no query carried
//   node src/main.ts --server <url>   a server other than Sample.WebServer

import { createCommands, createScry, httpTransport, ScryRequestError } from "scry-client";
import { schema, type Commands, type Sources } from "./scry.generated.ts";

/** Where Sample.WebServer listens, per its launchSettings.json. */
const server = valueOf("--server") ?? "http://localhost:5000";

// No container and no host: a transport pointed at the endpoint MapScry was given, and the generated
// description of the model.
// begin-snippet: typescriptClientSetup
const scry = createScry<Sources>(schema, {
  transport: httpTransport({ endpoint: `${server}/api/query` }),
  onSchemaStale: _ => console.warn(`Generated against ${_.client}, and the server is on ${_.server}: run npm run generate.`)
});
const commands = createCommands<Commands>(scry);
// end-snippet

async function main(): Promise<number> {
  try {
    if (process.argv.includes("--live")) {
      if (process.argv.includes("--loop")) {
        await readOrders();
        return 0;
      }

      await watchOrders();
      return 0;
    }

    if (process.argv.includes("--pages")) {
      await showPagesByOffset();
      await showPagesByCursor();
      return 0;
    }

    const reprice = valueOf("--reprice");
    if (reprice !== undefined) {
      return await repriceOrder(Number(reprice));
    }

    if (process.argv.includes("--photos")) {
      await showPhotos();
      return 0;
    }

    await showActiveEmployees();
    await showRegions();
    await showActiveCount();
    return 0;
  } catch (error) {
    if (error instanceof ScryRequestError) {
      console.error(`The server refused it (${error.code}): ${error.message}`);
      return 1;
    }

    // fetch's own failure: nothing answered.
    if (error instanceof TypeError) {
      console.error(`Cannot reach ${server}: ${error.message}`);
      console.error("Start the server with: dotnet run --project samples/Sample.WebServer");
      return 1;
    }

    throw error;
  }
}

// A filter, an ordering, and a projection that reaches through two navigations. Nothing runs here: the
// query is the wire request, validated against the allow-list on the server, rebound to the real
// Employee, and run through EF Core. A value read through a navigation may be null, and is typed so.
// begin-snippet: typescriptQuery
async function showActiveEmployees(): Promise<void> {
  const rows = await scry.employee
    .where(_ => _.active)
    .orderBy(_ => _.name)
    .select(_ => ({ name: _.name, status: _.status, manager: _.manager.name, department: _.department.name }))
    .toList();

  writeTable(
    ["Name", "Status", "Manager", "Department"],
    rows.map(_ => [_.name, _.status, _.manager ?? "", _.department ?? ""]));
}
// end-snippet

// Grouping and aggregation happen on the server, so what crosses the wire is one row per region rather
// than every order. A decimal arrives as its exact text, never as a JavaScript number.
// begin-snippet: typescriptGroupBy
async function showRegions(): Promise<void> {
  const rows = await scry.order
    .groupBy(_ => _.region)
    .select(group => ({ region: group.key, total: group.sum(_ => _.amount), count: group.count() }))
    .toList();

  writeTable(
    ["Region", "Total", "Orders"],
    rows.map(_ => [_.region, _.total, String(_.count)]));
}
// end-snippet

// A terminal that takes a condition of its own sends it the same way.
// begin-snippet: typescriptCount
async function showActiveCount(): Promise<void> {
  const active = await scry.employee.count(_ => _.active);
  console.log(`${active} active employees`);
  console.log();
}
// end-snippet

// A live query answers with the whole current result, again each time it changes, until it is stopped,
// which is also what tells the server the subscription is over. A connection that drops is asked for
// again on its own, naming the last answer held.
// begin-snippet: typescriptLive
async function watchOrders(): Promise<void> {
  console.log("Watching orders. Ctrl+C to stop.");
  console.log();
  const orders = scry.order
    .orderBy(_ => _.id)
    .select(_ => ({ id: _.id, region: _.region, amount: _.amount }))
    .live();

  await new Promise<void>(resolve => {
    const stop = orders.subscribe(
      answer => {
        console.log(new Date().toLocaleTimeString());
        writeTable(["Id", "Region", "Amount"], answer.map(_ => [String(_.id), _.region, _.amount]));
      },
      error => {
        console.error(`The live query ended: ${(error as Error).message}`);
        resolve();
      });
    process.once("SIGINT", () => {
      stop();
      resolve();
    });
  });
}
// end-snippet

// The same live query read in a loop. The next answer is not read until the loop comes back for it,
// so a slow reader is never handed a queue of them; leaving the loop ends the subscription, and so
// does Ctrl+C, which ends the process.
// begin-snippet: typescriptLiveLoop
async function readOrders(): Promise<void> {
  console.log("Watching orders. Ctrl+C to stop.");
  console.log();
  const orders = scry.order
    .orderBy(_ => _.id)
    .select(_ => ({ id: _.id, region: _.region, amount: _.amount }))
    .live();

  for await (const answer of orders) {
    console.log(new Date().toLocaleTimeString());
    writeTable(["Id", "Region", "Amount"], answer.map(_ => [String(_.id), _.region, _.amount]));
  }
}
// end-snippet

// Offset paging: skip to each page's start, and stop where a page says there is no more.
// begin-snippet: typescriptPaging
async function showPagesByOffset(): Promise<void> {
  const size = 2;
  for (let index = 0; ; index++) {
    const page = await scry.employee
      .orderBy(_ => _.name)
      .skip(index * size)
      .select(_ => ({ name: _.name, status: _.status }))
      .page(size);

    console.log(`Page ${index + 1}, by offset`);
    writeTable(["Name", "Status"], page.items.map(_ => [_.name, _.status]));
    if (!page.hasMore) {
      return;
    }
  }
}
// end-snippet

// Keyset paging: each page's cursor is where the next one starts, so the server seeks past the last
// row rather than counting the rows before it.
// begin-snippet: typescriptKeyset
async function showPagesByCursor(): Promise<void> {
  let cursor: string | undefined;
  for (let number = 1; ; number++) {
    const page = await scry.employee
      .orderBy(_ => _.name)
      .select(_ => ({ name: _.name, status: _.status }))
      .page(2, cursor);

    console.log(`Page ${number}, by cursor`);
    writeTable(["Name", "Status"], page.items.map(_ => [_.name, _.status]));
    if (!page.hasMore) {
      return;
    }

    cursor = page.cursor;
  }
}
// end-snippet

// A command, and its outcome. Most finish within the server's sync window and print at once; one still
// running when the client stops waiting is Pending, and its completion is what finishes it. Every
// outcome has one, which is the outcome itself where it is already final, so a console that has
// nowhere else to show pending work awaits it either way.
// begin-snippet: typescriptCommand
async function repriceOrder(id: number): Promise<number> {
  const sent = await commands.send.repriceOrder({ id });
  if (sent.status === "Pending") {
    console.log(`Order ${id} is being repriced…`);
  }

  const outcome = await sent.completion;
  switch (outcome.status) {
    case "Completed":
      console.log(`Order ${id} repriced.`);
      return 0;
    case "Failed":
      console.error(`Order ${id} was not repriced: ${outcome.error}`);
      return 1;
    case "Unknown":
      console.error(`Whether order ${id} was repriced is unknown: ${outcome.error}`);
      return 1;
  }
}
// end-snippet

// A photo is an attachment, which no query carries: each row comes back with a handle made from its
// key, and opening one fetches the bytes as a request of its own. One employee has none.
// begin-snippet: typescriptAttachment
async function showPhotos(): Promise<void> {
  const employees = await scry.employee
    .orderBy(_ => _.id)
    .select(_ => ({ id: _.id, name: _.name, photo: _.photo }))
    .toList();

  const rows: string[][] = [];
  for (const employee of employees) {
    const photo = await employee.photo.open();
    if (photo === null) {
      rows.push([employee.name, "none"]);
      continue;
    }

    const bytes = await photo.bytes();
    rows.push([employee.name, `${bytes.length} bytes of ${photo.contentType}`]);
  }

  writeTable(["Name", "Photo"], rows);
}
// end-snippet

function valueOf(name: string): string | undefined {
  const index = process.argv.indexOf(name);
  if (index < 0) {
    return undefined;
  }

  return process.argv[index + 1];
}

// Nothing to do with Scry: enough column alignment to make the rows readable in a terminal.
function writeTable(headers: readonly string[], rows: readonly (readonly string[])[]): void {
  const widths = headers.map((header, column) => Math.max(header.length, ...rows.map(_ => _[column]!.length)));
  const line = (cells: readonly string[]) => cells.map((cell, column) => cell.padEnd(widths[column]!)).join("  ");
  console.log(line(headers));
  console.log(line(widths.map(_ => "-".repeat(_))));
  for (const row of rows) {
    console.log(line(row));
  }

  console.log();
}

process.exitCode = await main();
