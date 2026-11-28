// What the generated types say, checked by the compiler alone: `npm run typecheck` fails if any of
// these stops holding. Nothing here runs.

import type * as scry from "../src/runtime/index.ts";
import type {
  Commands,
  CreateShiftCommand,
  DayOfWeek,
  EmployeeQuery,
  EmployeeRow,
  OrderLineQuery,
  OrderLineRow,
  OrderQuery,
  OrderRow,
  Perks,
  ShiftCreated,
  SittingQuery,
  Sources,
  Status,
  VehicleQuery,
  VehicleRow
} from "./generated/test-model.ts";

type Equal<A, B> = (<T>() => T extends A ? 1 : 2) extends (<T>() => T extends B ? 1 : 2) ? true : false;
type Holds<T extends true> = T;

export type Checks = [
  // A member is an expression of the value a row delivers.
  Holds<Equal<EmployeeQuery["name"], scry.Expr<string>>>,
  Holds<Equal<EmployeeQuery["status"], scry.Expr<Status>>>,
  Holds<Equal<EmployeeQuery["perks"], scry.Expr<scry.Flags<Perks>>>>,
  Holds<Equal<EmployeeQuery["managerId"], scry.Expr<number | null>>>,

  // Through a navigation that may be absent, every value may be null, however far along.
  Holds<Equal<EmployeeQuery["manager"]["name"], scry.Expr<string | null>>>,
  Holds<Equal<EmployeeQuery["manager"]["manager"]["id"], scry.Expr<number | null>>>,

  // A collection is asked about, its function given each element.
  Holds<Equal<OrderQuery["lines"], scry.Collection<OrderLineQuery, OrderLineRow>>>,
  Holds<Equal<OrderQuery["tags"], scry.Collection<scry.Expr<string>>>>,
  Holds<Equal<SittingQuery["alternates"], scry.Collection<scry.Expr<DayOfWeek>>>>,
  Holds<Equal<EmployeeQuery["manager"]["previousAddresses"], EmployeeQuery["previousAddresses"]>>,

  // Values a number cannot hold exactly are not numbers.
  Holds<Equal<OrderRow["sku"], bigint>>,
  Holds<Equal<OrderRow["amount"], scry.Decimal>>,
  Holds<Equal<OrderRow["discount"], scry.Decimal | null>>,
  Holds<Equal<OrderRow["placed"], scry.DateTime>>,

  // A derived model has its base's members.
  Holds<Equal<VehicleQuery["name"], scry.Expr<string>>>,
  Holds<Equal<VehicleRow["name"], string>>,

  Holds<Equal<Sources["employee"], scry.SourceTypes<EmployeeQuery, EmployeeRow>>>,
  Holds<Equal<Commands["createShift"], scry.CommandTypes<CreateShiftCommand, ShiftCreated>>>,
  Holds<Equal<Commands["sealContract"]["result"], void>>
];

// A row is what a query projecting nothing returns, which reads no navigation.
// @ts-expect-error
export type NoNavigationInARow = EmployeeRow["manager"];

// No query reads an attachment's bytes: a query returns the member, and a row carries the handle that
// fetches them by the row's key.
export type Attachments = [
  Holds<Equal<import("./generated/test-model.ts").ContractQuery["document"], scry.AttachmentMember>>,
  Holds<Equal<import("./generated/test-model.ts").ContractRow["document"], scry.Attachment>>
];

// The pipeline's grammar, held by the types: what the server would refuse does not compile.

import { eq, resultOf } from "../src/index.ts";
import type { CommandOutcome, LiveQuery, Page, Scry, ScryCommands } from "../src/index.ts";

declare const client: Scry<Sources>;

// @ts-expect-error thenBy() refines an ordering, so it follows one.
client.employee.thenBy(_ => _.name);

// @ts-expect-error A projection is the last shape the rows take: nothing filters it.
client.employee.select(_ => ({ name: _.name })).where(_ => _);

// @ts-expect-error A grouped query has to project.
void client.employee.groupBy(_ => _.status).toList();

// @ts-expect-error Nothing orders a grouping.
client.order.groupBy(_ => _.region).orderBy(_ => _);

// @ts-expect-error A terminal's condition cannot follow a projection.
void client.employee.select(_ => ({ name: _.name })).first(_ => _);

// @ts-expect-error A set operation combines rows of one shape.
client.order.select(_ => ({ region: _.region })).union(client.order.select(_ => ({ amount: _.amount })));

// @ts-expect-error JavaScript's own comparison asks nothing of the server, and does not type-check.
client.order.where(_ => _.amount > 5);

// @ts-expect-error A constant of another type than the member's.
client.employee.where(_ => eq(_.active, 5));

// What each query returns, inferred from what it was written with.

const projected = client.order.select(_ => ({
  region: _.region,
  tags: _.tags,
  lines: _.lines.select(l => ({ sku: l.sku })),
  count: _.lines.count(),
  department: { name: _.region }
}));
const grouped = client.order.groupBy(_ => _.region).select(g => ({ key: g.key, total: g.sum(_ => _.amount) }));
const joined = client.employee.leftJoin(client.department, _ => _.departmentId, _ => _.id, (employee, department) => ({ employee: employee.name, department: department.name }));

// @ts-expect-error An attachment is not a value to compare.
client.contract.where(_ => eq(_.document, new Uint8Array()));

const documents = client.contract.select(_ => ({ id: _.id, document: _.document }));

export type Returns = [
  Holds<Equal<Awaited<ReturnType<typeof documents.toList>>, { id: number; document: scry.Attachment }[]>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.toList>>, EmployeeRow[]>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.count>>, number>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.longCount>>, bigint>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.firstOrDefault>>, EmployeeRow | null>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.page>>, Page<EmployeeRow>>>,
  Holds<Equal<Awaited<ReturnType<typeof projected.toList>>, { region: string; tags: string[]; lines: { sku: string }[]; count: number; department: { name: string } }[]>>,
  Holds<Equal<Awaited<ReturnType<typeof grouped.toList>>, { key: string; total: scry.Decimal }[]>>,
  Holds<Equal<Awaited<ReturnType<typeof joined.toList>>, { employee: string; department: string | null }[]>>
];

// A terminal takes its signal last, after the condition where it has one, which it may then leave out.

declare const signal: AbortSignal;

const counted = client.employee.count({ signal });
const countedWhere = client.employee.count(_ => _.active, { signal });
const foundWhere = client.employee.firstOrDefault(_ => _.active, { signal });
const summed = client.order.sum(_ => _.amount, { signal });
const pagedAtTheServersSize = client.employee.page({ signal });
const pagedBySize = client.employee.page(10, { signal });
const pagedAfter = client.employee.page(10, "eyJrIjpbXX0.c2ln", { signal });
const streamed = client.employee.stream({ signal });
const projectedFirst = projected.first({ signal });
const watched = client.employee.live({ signal });
const watchedWhere = client.employee.liveCount(_ => _.active, { signal });
const watchedProjection = projected.liveFirstOrDefault({ signal });

// @ts-expect-error all() needs its condition, signal or not.
void client.employee.all({ signal });

// @ts-expect-error A projection's rows take no condition, so a function is not their options.
void projected.first(_ => _.count);

// @ts-expect-error Nor do a projection's live terminals.
void projected.liveCount(_ => _.count);

// @ts-expect-error A signal is an AbortSignal.
void client.employee.toList({ signal: true });

// @ts-expect-error A cursor follows a size, even one left to the server.
void client.employee.page("eyJrIjpbXX0.c2ln", { signal });

export type Cancellable = [
  Holds<Equal<typeof counted, Promise<number>>>,
  Holds<Equal<typeof countedWhere, Promise<number>>>,
  Holds<Equal<typeof foundWhere, Promise<EmployeeRow | null>>>,
  Holds<Equal<typeof summed, Promise<scry.Decimal>>>,
  Holds<Equal<typeof pagedAtTheServersSize, Promise<Page<EmployeeRow>>>>,
  Holds<Equal<typeof pagedBySize, Promise<Page<EmployeeRow>>>>,
  Holds<Equal<typeof pagedAfter, Promise<Page<EmployeeRow>>>>,
  Holds<Equal<typeof streamed, AsyncIterable<EmployeeRow>>>,
  Holds<Equal<typeof projectedFirst, Promise<{ region: string; tags: string[]; lines: { sku: string }[]; count: number; department: { name: string } }>>>,
  Holds<Equal<typeof watched, LiveQuery<EmployeeRow[]>>>,
  Holds<Equal<typeof watchedWhere, LiveQuery<number>>>,
  Holds<Equal<typeof watchedProjection, LiveQuery<Awaited<typeof projectedFirst> | null>>>
];

// A query's own headers leave it the query it was, so what may follow is what could before.

const headedOrdering = client.employee.orderBy(_ => _.name).withHeader("X-Correlation", "1").thenBy(_ => _.id);
const headedProjection = projected.withHeaders(_ => _.set("X-Correlation", "1")).onResponseHeaders(_ => _.get("X-Trace")).distinct();

// @ts-expect-error A header's value is text.
void client.employee.withHeader("X-Retry", 1);

export type Headed = [
  Holds<Equal<Awaited<ReturnType<typeof headedOrdering.toList>>, EmployeeRow[]>>,
  Holds<Equal<Awaited<ReturnType<typeof headedProjection.toList>>, Awaited<ReturnType<typeof projected.toList>>>>
];

// A command is sent with its payload, written as a constant is, and completes with its result.

declare const commands: ScryCommands<Commands>;

commands.send.createShift({ day: new Date(), name: "Late", perks: [["Parking", "Gym"], "None"] });
commands.send.renameShift({ id: 1, name: "Night" });

// @ts-expect-error A payload member the command needs is missing.
commands.send.renameShift({ name: "Night" });

// @ts-expect-error A member the command does not have.
commands.send.sealContract({ contractId: 1, sealedBy: "me" });

// @ts-expect-error A command the model does not declare.
commands.send.deleteShift({ id: 1 });

// @ts-expect-error Capabilities are asked by the command's name.
commands.can("createShifts");

const created = commands.send.createShift({ day: "2026-03-04", name: "Late", perks: [] });

export type Sends = [
  Holds<Equal<Awaited<typeof created>, CommandOutcome<ShiftCreated>>>,
  Holds<Equal<ReturnType<typeof resultOf<ShiftCreated>>, ShiftCreated>>,
  Holds<Equal<Extract<Awaited<typeof created>, { status: "Completed" }>["result"], ShiftCreated>>,
  Holds<Equal<Extract<Awaited<ReturnType<typeof commands.send.renameShift>>, { status: "Completed" }>["result"], void>>
];
