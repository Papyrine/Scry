// What docs/typescript.md shows beyond what main.ts runs, kept here so that the sample's type-check
// compiles every example the page quotes. Nothing calls these.

import { and, createBatch, createCommands, createScry, eq, gt, httpTransport, resultOf, startsWith } from "scry-client";
import { schema, type Commands, type Sources } from "./scry.generated.ts";

declare function token(): string;
declare function show(text: string): void;
declare const correlationId: string;

// begin-snippet: typescriptTransport
const scry = createScry<Sources>(schema, {
  transport: httpTransport({
    endpoint: "https://example.test/api/query",
    headers: () => ({ Authorization: `Bearer ${token()}` })
  })
});
// end-snippet

export async function where(): Promise<void> {
  // begin-snippet: typescriptWhere
  const rows = await scry.employee
      .where(_ => and(_.active, eq(_.status, "FullTime"), startsWith(_.manager.name, "A")))
      .orderBy(_ => _.name)
      .select(_ => (
          {
            name: _.name,
            manager: _.manager.name,
            department: {
              name: _.department.name
            }
          }))
      .toList();
  // { name: string; manager: string | null; department: { name: string | null } }[]
  // end-snippet
  show(JSON.stringify(rows));
}

export async function stream(): Promise<void> {
  // begin-snippet: typescriptStream
  let stream = scry
      .order
      .orderBy(_ => _.id)
      .select(_ => ({id: _.id, amount: _.amount}))
      .stream();
  for await (const order of stream) {
    show(`${order.id}: ${order.amount}`);
  }
  // end-snippet
}

export async function batch(): Promise<void> {
  // begin-snippet: typescriptBatch
  const batch = createBatch(scry);
  const count = batch.add(_ => _.employee.count());
  const names =
      batch
          .add(_ => _.employee.orderBy(_ => _.name)
              .select(_ => ({ name: _.name }))
              .toList());
  await batch.send();

  show(`${await count} employees, the first ${(await names)[0]?.name}`);
  // end-snippet
}

export async function headers(): Promise<string | null> {
  let trace: string | null = null;
  // begin-snippet: typescriptHeaders
  const rows = await scry.order
    .withHeader("X-Correlation", correlationId)
    .onResponseHeaders(_ => trace = _.get("X-Trace"))
    .where(_ => gt(_.amount, 100))
    .select(_ => ({ id: _.id, amount: _.amount }))
    .toList();
  // end-snippet
  show(`${rows.length} orders`);
  return trace;
}

export async function cancel(): Promise<void> {
  // begin-snippet: typescriptCancel
  const signal = AbortSignal.timeout(5_000);
  const active =
      await scry
          .employee
          .count(_ => _.active, { signal });
  const first =
      await scry
          .employee
          .orderBy(_ => _.name)
          .select(_ => ({ name: _.name }))
          .page(20, { signal });
  // end-snippet
  show(`${active} active, the first page starting at ${first.items[0]?.name}`);
}

export function liveCount(): () => void {
  // begin-snippet: typescriptLiveCount
  const stop = scry.employee
    .liveCount(_ => _.active)
    .subscribe(
      active => show(`${active} active`),
      error => show(`The live query ended: ${(error as Error).message}`));
  // end-snippet
  return stop;
}

export async function hire(): Promise<void> {
  // begin-snippet: typescriptCommandResult
  const commands = createCommands<Commands>(scry);
  await commands.ready;
  if (!commands.can("createEmployee")) {
    return;
  }

  const hired =
      await commands
          .send
          .createEmployee({name: "Ann", departmentId: 1, status: "FullTime"});
  const {id} = resultOf(await hired.completion);
  const deletable =
      await scry.employee
          .where(_ => eq(_.id, id))
          .select(_ => ({name: _.name, deletable: _.canDeleteEmployee}))
          .single();
  // end-snippet
  show(JSON.stringify(deletable));
}
