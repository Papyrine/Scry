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

// No query reads an attachment: it is fetched by the row's key.
// @ts-expect-error
export type NoAttachmentInAQuery = import("./generated/test-model.ts").ContractQuery["document"];

// The pipeline's grammar, held by the types: what the server would refuse does not compile.

import { eq } from "../src/index.ts";
import type { Page, Scry } from "../src/index.ts";

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

export type Returns = [
  Holds<Equal<Awaited<ReturnType<typeof client.employee.toList>>, EmployeeRow[]>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.count>>, number>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.longCount>>, bigint>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.firstOrDefault>>, EmployeeRow | null>>,
  Holds<Equal<Awaited<ReturnType<typeof client.employee.page>>, Page<EmployeeRow>>>,
  Holds<Equal<Awaited<ReturnType<typeof projected.toList>>, { region: string; tags: string[]; lines: { sku: string }[]; count: number; department: { name: string } }[]>>,
  Holds<Equal<Awaited<ReturnType<typeof grouped.toList>>, { key: string; total: scry.Decimal }[]>>,
  Holds<Equal<Awaited<ReturnType<typeof joined.toList>>, { employee: string; department: string | null }[]>>
];
