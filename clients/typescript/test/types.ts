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
  Holds<Equal<OrderQuery["lines"], scry.Collection<OrderLineQuery>>>,
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
