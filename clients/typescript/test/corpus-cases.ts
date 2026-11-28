// The corpus's shapes written as TypeScript queries: every shape WireShapeTests records on the .NET side,
// by the C# label it was written as. corpus.test.ts compares the request each sends, and
// responses.test.ts reads back the answer the server gave it.


import {
  abs, acos, add, addDays, addHours, addMilliseconds, addMinutes, addMonths, addSeconds, addYears, and, asin, atan, atan2,
  ceiling, coalesce, compare, concat, cond, contains, cos, dateOf, dateOnly, dateTime, day, dayNumber,
  dayOfWeek, dayOfYear, degreesToRadians, div, elementAt, endsWith, eq, exp, firstChar, floor, ge, gt, hasFlag,
  hasValue, hour, hours, ignoringCase, indexOf, isIn, isNull, isNullOrEmpty, isNullOrWhiteSpace, lastChar, le,
  length, log, log10, lt, matchingCase, max, microsecond, microseconds, millisecond, milliseconds, min, minute,
  minutes, mod, month, mul, nanosecond, nanoseconds, ne, neg, not, pow, radiansToDegrees, replace, round, second,
  seconds, sign, sin, sqrt, startsWith, sub, substring, tan, timeOfDay, timeOnly, toBoolean, toByte,
  toDecimal, toDouble, toInt16, toInt32, toInt64, toLower, toSingle, toText, toUpper, trim, trimEnd, trimStart,
  truncate, unixMilliseconds, unixSeconds, year
} from "../src/index.ts";
import type { Scry } from "../src/index.ts";
import type { Sources } from "./generated/test-model.ts";

// Every kind of character the wire's encoder escapes, and some it writes as they are.
const escapes = `quote " backslash \\ <tag> & 'apos' + é 漢 👪 ${String.fromCharCode(0x2028)} ${String.fromCharCode(1)} ${String.fromCharCode(9)}`;

/** Every corpus shape written as a TypeScript query, by the C# it was written as, run on `scry`. */
export function corpusCases(scry: Scry<Sources>): Record<string, () => Promise<unknown>> {
  return {
  // Filtering.
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Active).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => _.active).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => !_.Active).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => not(_.active)).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Status == wanted && _.Name.StartsWith(prefix)).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => and(eq(_.status, "Contractor"), startsWith(_.name, "A"))).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.ManagerId == null).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => isNull(_.managerId)).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.ManagerId.HasValue).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => hasValue(_.managerId)).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Avatar == avatar).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => eq(_.avatar, new Uint8Array([1, 2, 3]))).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Address.Country == "UK").Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => eq(_.address.country, "UK")).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Manager!.Name == "Alice").Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => eq(_.manager.name, "Alice")).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Perks.HasFlag(Perks.Parking | Perks.Gym)).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => hasFlag(_.perks, ["Parking", "Gym"])).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => regions.Contains(_.Region)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => isIn(_.region, ["North", "West"])).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Amount >= 100m && _.Amount < 250m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => and(ge(_.amount, 100), lt(_.amount, 250))).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Amount - (_.Discount ?? 0m) > 90m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => gt(sub(_.amount, coalesce(_.discount, 0)), 90)).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Amount * 2m / 4m + 1m <= 100m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => le(add(div(mul(_.amount, 2), 4), 1), 100)).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Region.Length % 2 == 0).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => eq(mod(length(_.region), 2), 0)).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => -_.Amount < 0m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => lt(neg(_.amount), 0)).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Grade == 'A').Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => eq(_.grade, "A")).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Quantity == 7u && _.Sku == ulong.MaxValue).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => and(eq(_.quantity, 7), eq(_.sku, 18446744073709551615n))).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Placed > new DateTime(2026, 1, 1)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => gt(_.placed, "2026-01-01")).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Id < beyondInt).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => lt(_.id, 5000000000n)).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Active == active).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => eq(_.active, true)).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Shift>("Shift").Where(_ => _.Day >= new Date(2026, 1, 1)).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.shift.where(_ => ge(_.day, "2026-01-01")).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Ticket>("Ticket").Where(_ => _.Token != token).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.ticket.where(_ => ne(_.token, "5f2b8a3c-1d4e-4f60-9a7b-8c9d0e1f2a3b")).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Name != escapes).Select(_ => new {Größe = _.Name}).ToListAsync()`]:
    () => scry.employee.where(_ => ne(_.name, escapes)).select(_ => ({ größe: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Name.Contains("LIC", StringComparison.OrdinalIgnoreCase)).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => contains(ignoringCase(_.name), "LIC")).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Name.Equals("Alice", StringComparison.Ordinal)).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => eq(matchingCase(_.name), "Alice")).select(_ => ({ name: _.name })).toList(),

  // Ordering.
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").OrderByDescending(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.orderByDescending(_ => _.name).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Status).ThenBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.orderBy(_ => _.status).thenBy(_ => _.name).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Status).ThenByDescending(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.orderBy(_ => _.status).thenByDescending(_ => _.name).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Reverse().Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.orderBy(_ => _.name).reverse().select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Skip(2).Take(1).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.orderBy(_ => _.name).skip(2).take(1).select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Order>("Order").OrderBy(_ => int.Parse(_.Code)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.orderBy(_ => toInt32(_.code)).select(_ => ({ region: _.region, amount: _.amount })).toList(),

  // Projection.
  [`_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Select(_ => new {_.Name, Manager = _.Manager!.Name}).ToListAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name, manager: _.manager.name })).toList(),
  [`_ => _.Source<Employee>("Employee").Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name))).ToListAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name, department: { name: _.department.name } })).toList(),
  [`_ => _.Source<Employee>("Employee").Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name.ToUpper()))).ToListAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name, department: { name: toUpper(_.department.name) } })).toList(),
  [`_ => _.Source<Employee>("Employee").Select(_ => new {_.Name, _.Address.City, _.Address.Country}).ToListAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name, city: _.address.city, country: _.address.country })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new OrderRow(_.Region == "North" ? "N" : "S", _.Amount - (_.Discount ?? 0m))).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: cond(eq(_.region, "North"), "N", "S"), amount: sub(_.amount, coalesce(_.discount, 0)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {Label = $"{_.Region}-{_.Quantity}"}).ToListAsync()`]:
    () => scry.order.select(_ => ({ label: concat(_.region, "-", _.quantity) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Region}).Distinct().ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region })).distinct().toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Tags = _.Tags.ToList()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, tags: _.tags })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Lines = _.Lines.Where(l => l.Quantity > 1).Select(l => new {l.Sku, l.Quantity}).ToList()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, lines: _.lines.where(l => gt(l.quantity, 1)).select(l => ({ sku: l.sku, quantity: l.quantity })) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Prices = _.Lines.Select(l => l.Price * 2m).ToList()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, prices: _.lines.select(l => mul(l.price, 2)) })).toList(),

  // Grouping.
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Total = _.Sum(_ => _.Amount), Rows = _.Count()}).ToListAsync()`]:
    () => scry.order.groupBy(_ => _.region).select(g => ({ key: g.key, total: g.sum(_ => _.amount), rows: g.count() })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Low = _.Min(_ => _.Amount), High = _.Max(_ => _.Amount), Mean = _.Average(o => o.Amount)}).ToListAsync()`]:
    () => scry.order.groupBy(_ => _.region).select(g => ({ key: g.key, low: g.min(_ => _.amount), high: g.max(_ => _.amount), mean: g.average(_ => _.amount) })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => new {_.Region, _.Grade}).Select(_ => new {_.Key.Region, _.Key.Grade, Total = _.Sum(_ => _.Amount)}).ToListAsync()`]:
    () => scry.order.groupBy(_ => ({ region: _.region, grade: _.grade })).select(g => ({ region: g.key.region, grade: g.key.grade, total: g.sum(_ => _.amount) })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Placed.DayOfWeek).Select(_ => new {Day = _.Key, Count = _.Count()}).ToListAsync()`]:
    () => scry.order.groupBy(_ => dayOfWeek(_.placed)).select(g => ({ day: g.key, count: g.count() })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region.ToUpper()).Select(_ => new {Region = _.Key, Total = _.Sum(_ => _.Amount)}).ToListAsync()`]:
    () => scry.order.groupBy(_ => toUpper(_.region)).select(g => ({ region: g.key, total: g.sum(_ => _.amount) })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region, (region, orders) => new {Region = region, Total = orders.Sum(_ => _.Amount)}).ToListAsync()`]:
    () => scry.order.groupBy(_ => _.region).select(g => ({ region: g.key, total: g.sum(_ => _.amount) })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Where(_ => _.Sum(o => o.Amount) > 100m && _.Key != "South").Select(_ => new {_.Key, Total = _.Sum(_ => _.Amount)}).ToListAsync()`]:
    () => scry.order.groupBy(_ => _.region).where(g => and(gt(g.sum(_ => _.amount), 100), ne(g.key, "South"))).select(g => ({ key: g.key, total: g.sum(_ => _.amount) })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Big = _.Count(_ => _.Amount > 90m), Graded = _.Count(_ => _.Grade == 'A')}).ToListAsync()`]:
    () => scry.order.groupBy(_ => _.region).select(g => ({ key: g.key, big: g.count(_ => gt(_.amount, 90)), graded: g.count(_ => eq(_.grade, "A")) })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Grades = _.Select(_ => _.Grade).Distinct().Count()}).ToListAsync()`]:
    () => scry.order.groupBy(_ => _.region).select(g => ({ key: g.key, grades: g.values(_ => _.grade).distinct().count() })).toList(),
  [`_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Codes = string.Join(",", _.Select(_ => _.Code))}).ToListAsync()`]:
    () => scry.order.groupBy(_ => _.region).select(g => ({ key: g.key, codes: g.values(_ => _.code).join(",") })).toList(),

  // Joins.
  [`client.Source<Employee>("Employee").Join(client.Source<Department>("Department"), _ => _.DepartmentId, _ => _.Id, (employee, department) => new {Employee = employee.Name, Department = department.Name}).ToListAsync()`]:
    () => scry.employee.join(scry.department, _ => _.departmentId, _ => _.id, (employee, department) => ({ employee: employee.name, department: department.name })).toList(),
  [`client.Source<Employee>("Employee").Join(client.Source<Department>("Department").Where(_ => _.Name == "Engineering"), _ => _.DepartmentId, _ => _.Id, (employee, department) => new {Employee = employee.Name, Department = department.Name}).ToListAsync()`]:
    () => scry.employee.join(scry.department.where(_ => eq(_.name, "Engineering")), _ => _.departmentId, _ => _.id, (employee, department) => ({ employee: employee.name, department: department.name })).toList(),
  [`client.Source<Employee>("Employee").LeftJoin(client.Source<Department>("Department"), _ => _.DepartmentId, _ => _.Id, (employee, department) => new {Employee = employee.Name, Department = department!.Name}).ToListAsync()`]:
    () => scry.employee.leftJoin(scry.department, _ => _.departmentId, _ => _.id, (employee, department) => ({ employee: employee.name, department: department.name })).toList(),
  [`client.Source<Employee>("Employee").RightJoin(client.Source<Ticket>("Ticket"), _ => _.Id, _ => _.Id, (employee, ticket) => new {Employee = employee!.Name, Ticket = ticket.Name}).ToListAsync()`]:
    () => scry.employee.rightJoin(scry.ticket, _ => _.id, _ => _.id, (employee, ticket) => ({ employee: employee.name, ticket: ticket.name })).toList(),
  [`client.Source<Department>("Department").GroupJoin(client.Source<Employee>("Employee"), _ => _.Id, _ => _.DepartmentId, (department, employees) => new {Department = department.Name, Size = employees.Count()}).ToListAsync()`]:
    () => scry.department.groupJoin(scry.employee, _ => _.id, _ => _.departmentId, (department, employees) => ({ department: department.name, size: employees.count() })).toList(),
  [`client.Source<Order>("Order").Join(client.Source<Order>("Order"), _ => new {_.Region, _.Grade}, _ => new {_.Region, _.Grade}, (outer, inner) => new {outer.Code, Matched = inner.Amount}).ToListAsync()`]:
    () => scry.order.join(scry.order, _ => ({ region: _.region, grade: _.grade }), _ => ({ region: _.region, grade: _.grade }), (outer, inner) => ({ code: outer.code, matched: inner.amount })).toList(),
  [`client.Source<Order>("Order").Join(client.Source<Order>("Order").Where(_ => _.Grade == 'A').OrderByDescending(_ => _.Amount).Take(1), _ => _.Region, _ => _.Region, (outer, inner) => new {outer.Code, Matched = inner.Amount}).ToListAsync()`]:
    () => scry.order.join(scry.order.where(_ => eq(_.grade, "A")).orderByDescending(_ => _.amount).take(1), _ => _.region, _ => _.region, (outer, inner) => ({ code: outer.code, matched: inner.amount })).toList(),

  // Set operations.
  [`client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Union(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, amount: _.amount })).union(scry.orderLine.select(_ => ({ region: _.sku, amount: _.price }))).toList(),
  [`client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Concat(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, amount: _.amount })).concat(scry.orderLine.select(_ => ({ region: _.sku, amount: _.price }))).toList(),
  [`client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Intersect(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, amount: _.amount })).intersect(scry.orderLine.select(_ => ({ region: _.sku, amount: _.price }))).toList(),
  [`client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Except(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, amount: _.amount })).except(scry.orderLine.select(_ => ({ region: _.sku, amount: _.price }))).toList(),
  [`client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Union(client.Source<Order>("Order").Where(_ => _.Grade == 'A').OrderBy(_ => _.Amount).Take(2).Select(_ => new OrderRow(_.Region, _.Amount))).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, amount: _.amount })).union(scry.order.where(_ => eq(_.grade, "A")).orderBy(_ => _.amount).take(2).select(_ => ({ region: _.region, amount: _.amount }))).toList(),

  // Collections.
  [`_ => _.Source<Order>("Order").SelectMany(_ => _.Lines).Select(_ => new LineRow(_.Sku, _.Quantity)).ToListAsync()`]:
    () => scry.order.selectMany(_ => _.lines).select(_ => ({ sku: _.sku, quantity: _.quantity })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Region == "North").SelectMany(_ => _.Lines).Where(_ => _.Quantity > 1).Select(_ => new LineRow(_.Sku, _.Quantity)).ToListAsync()`]:
    () => scry.order.where(_ => eq(_.region, "North")).selectMany(_ => _.lines).where(_ => gt(_.quantity, 1)).select(_ => ({ sku: _.sku, quantity: _.quantity })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Lines.Any(l => l.Price == 25m)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => _.lines.any(l => eq(l.price, 25))).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Lines.All(l => l.Quantity > 0)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()`]:
    () => scry.order.where(_ => _.lines.all(l => gt(l.quantity, 0))).select(_ => ({ region: _.region, amount: _.amount })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Lines = _.Lines.Count(l => l.Quantity > 1)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, lines: _.lines.count(l => gt(l.quantity, 1)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Total = _.Lines.Sum(l => l.Price), Mean = _.Lines.Average(l => l.Price), Low = _.Lines.Min(l => l.Price), High = _.Lines.Max(l => l.Price)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, total: _.lines.sum(l => l.price), mean: _.lines.average(l => l.price), low: _.lines.min(l => l.price), high: _.lines.max(l => l.price) })).toList(),
  [`_ => _.Source<Order>("Order").Where(_ => _.Tags.Contains("urgent")).Select(_ => new {_.Region}).ToListAsync()`]:
    () => scry.order.where(_ => _.tags.contains("urgent")).select(_ => ({ region: _.region })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Region, Total = _.Scores.Sum(), High = _.Scores.Max()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ region: _.region, total: _.scores.sum(), high: _.scores.max() })).toList(),
  [`_ => _.Source<Employee>("Employee").Where(_ => _.PreviousAddresses.Any(address => address.City == "Berlin")).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => _.previousAddresses.any(address => eq(address.city, "Berlin"))).select(_ => ({ name: _.name })).toList(),
  [`client.Source<Employee>("Employee").Where(_ => client.Source<Department>("Department").Where(_ => _.Name == "Sales").Select(_ => _.Id).Contains(_.DepartmentId)).Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    () => scry.employee.where(_ => isIn(_.departmentId, scry.department.where(_ => eq(_.name, "Sales")), _ => _.id)).select(_ => ({ name: _.name })).toList(),

  // Hierarchy.
  [`_ => _.Source<Asset>("Asset").OfType<Vehicle>().Select(_ => new {_.Name, _.Wheels}).ToListAsync()`]:
    () => scry.asset.ofType("vehicle").select(_ => ({ name: _.name, wheels: _.wheels })).toList(),
  [`_ => _.Source<Asset>("Asset").OfType<Building>().Where(_ => _.Floors > 1).Select(_ => new {_.Name, _.Floors}).ToListAsync()`]:
    () => scry.asset.ofType("building").where(_ => gt(_.floors, 1)).select(_ => ({ name: _.name, floors: _.floors })).toList(),

  // Terminals.
  [`_ => _.Source<Employee>("Employee").CountAsync()`]: () => scry.employee.count(),
  [`_ => _.Source<Employee>("Employee").CountAsync(_ => _.Active)`]: () => scry.employee.count(_ => _.active),
  [`_ => _.Source<Employee>("Employee").LongCountAsync()`]: () => scry.employee.longCount(),
  [`_ => _.Source<Employee>("Employee").LongCountAsync(_ => _.Active)`]: () => scry.employee.longCount(_ => _.active),
  [`_ => _.Source<Employee>("Employee").AnyAsync()`]: () => scry.employee.any(),
  [`_ => _.Source<Employee>("Employee").AnyAsync(_ => _.Active)`]: () => scry.employee.any(_ => _.active),
  [`_ => _.Source<Employee>("Employee").AllAsync(_ => _.Active)`]: () => scry.employee.all(_ => _.active),
  [`_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).FirstAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name })).first(),
  [`_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).SingleAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name })).single(),
  [`_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).SingleOrDefaultAsync()`]:
    () => scry.employee.select(_ => ({ name: _.name })).singleOrDefault(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).LastAsync()`]:
    () => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).last(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).LastOrDefaultAsync()`]:
    () => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).lastOrDefault(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ElementAtAsync(1)`]:
    () => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).elementAt(1),
  [`_ => _.Source<Order>("Order").MaxByAsync(_ => _.Amount)`]: () => scry.order.maxBy(_ => _.amount),
  [`_ => _.Source<Order>("Order").MinByAsync(_ => _.Amount)`]: () => scry.order.minBy(_ => _.amount),
  [`_ => _.Source<Order>("Order").SumAsync(_ => _.Amount)`]: () => scry.order.sum(_ => _.amount),
  [`_ => _.Source<Order>("Order").AverageAsync(_ => _.Amount)`]: () => scry.order.average(_ => _.amount),
  [`_ => _.Source<Order>("Order").MinAsync(_ => _.Amount)`]: () => scry.order.min(_ => _.amount),
  [`_ => _.Source<Order>("Order").MaxAsync(_ => _.Amount)`]: () => scry.order.max(_ => _.amount),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToPageAsync()`]:
    () => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).page(),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToPageAsync(2)`]:
    () => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).page(2),
  [`_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToPageAsync(2, "eyJrIjpbXX0.c2ln")`]:
    () => scry.employee.orderBy(_ => _.name).select(_ => ({ name: _.name })).page(2, "eyJrIjpbXX0.c2ln"),

  // Text.
  [`_ =>_.Source<Order>("Order").Select(_ => new {V = _.Region.Contains("or")}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: contains(_.region, "or") })).toList(),
  [`_ =>_.Source<Order>("Order").Select(_ => new {V = _.Region.StartsWith("No")}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: startsWith(_.region, "No") })).toList(),
  [`_ =>_.Source<Order>("Order").Select(_ => new {V = _.Region.EndsWith("th")}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: endsWith(_.region, "th") })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {A = _.Region.StartsWith('N'), B = _.Region.EndsWith('h'), C = _.Region.Contains('o')}).ToListAsync()`]:
    () => scry.order.select(_ => ({ a: startsWith(_.region, "N"), b: endsWith(_.region, "h"), c: contains(_.region, "o") })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.ToLower()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toLower(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.ToUpper()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toUpper(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = string.IsNullOrEmpty(_.Region)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: isNullOrEmpty(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = string.IsNullOrWhiteSpace(_.Region)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: isNullOrWhiteSpace(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Length}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: length(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Trim()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: trim(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.TrimStart()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: trimStart(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.TrimEnd()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: trimEnd(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Substring(1)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: substring(_.region, 1) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Substring(1, 2)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: substring(_.region, 1, 2) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.IndexOf('o')}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: indexOf(_.region, "o") })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Replace("o", "0")}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: replace(_.region, "o", "0") })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.FirstOrDefault()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: firstChar(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.LastOrDefault()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: lastChar(_.region) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Amount.ToString()}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toText(_.amount) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.CompareTo("South")}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: compare(_.region, "South") })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = string.Compare(_.Region, "South")}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: compare(_.region, "South") })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Amount.CompareTo(100m)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: compare(_.amount, 100) })).toList(),

  // Dates and times.
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Placed.Year, _.Placed.Month, _.Placed.Day, _.Placed.DayOfYear}).ToListAsync()`]:
    () => scry.order.select(_ => ({ year: year(_.placed), month: month(_.placed), day: day(_.placed), dayOfYear: dayOfYear(_.placed) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Placed.Hour, _.Placed.Minute, _.Placed.Second, _.Placed.Millisecond}).ToListAsync()`]:
    () => scry.order.select(_ => ({ hour: hour(_.placed), minute: minute(_.placed), second: second(_.placed), millisecond: millisecond(_.placed) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Placed.Microsecond, _.Placed.Nanosecond}).ToListAsync()`]:
    () => scry.order.select(_ => ({ microsecond: microsecond(_.placed), nanosecond: nanosecond(_.placed) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {_.Placed.DayOfWeek, _.Placed.Date, _.Placed.TimeOfDay}).ToListAsync()`]:
    () => scry.order.select(_ => ({ dayOfWeek: dayOfWeek(_.placed), date: dateOf(_.placed), timeOfDay: timeOfDay(_.placed) })).toList(),
  [`_ => _.Source<Shift>("Shift").Select(_ => new {_.Day.DayNumber}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ dayNumber: dayNumber(_.day) })).toList(),
  [`_ => _.Source<Shift>("Shift").Select(_ => new {_.Duration.Hours, _.Duration.Minutes, _.Duration.Seconds}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ hours: hours(_.duration), minutes: minutes(_.duration), seconds: seconds(_.duration) })).toList(),
  [`_ => _.Source<Shift>("Shift").Select(_ => new {_.Duration.Milliseconds, _.Duration.Microseconds, _.Duration.Nanoseconds}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ milliseconds: milliseconds(_.duration), microseconds: microseconds(_.duration), nanoseconds: nanoseconds(_.duration) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {D = Date.FromDateTime(_.Placed), T = Time.FromDateTime(_.Placed)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ d: dateOnly(_.placed), t: timeOnly(_.placed) })).toList(),
  [`_ => _.Source<Shift>("Shift").Select(_ => new {T = Time.FromTimeSpan(_.Duration), Stamp = _.Day.ToDateTime(_.Start)}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ t: timeOnly(_.duration), stamp: dateTime(_.day, _.start) })).toList(),
  [`_ => _.Source<Shift>("Shift").Select(_ => new {S = _.Stamped.ToUnixTimeSeconds(), Ms = _.Stamped.ToUnixTimeMilliseconds()}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ s: unixSeconds(_.stamped), ms: unixMilliseconds(_.stamped) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {A = _.Placed.AddYears(1), B = _.Placed.AddMonths(1), C = _.Placed.AddDays(1), D = _.Placed.AddHours(1)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ a: addYears(_.placed, 1), b: addMonths(_.placed, 1), c: addDays(_.placed, 1), d: addHours(_.placed, 1) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {A = _.Placed.AddMinutes(1), B = _.Placed.AddSeconds(1), C = _.Placed.AddMilliseconds(1)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ a: addMinutes(_.placed, 1), b: addSeconds(_.placed, 1), c: addMilliseconds(_.placed, 1) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = _.Placed.CompareTo(new(2026, 1, 1))}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: compare(_.placed, "2026-01-01") })).toList(),

  // Numbers.
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Abs(_.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: abs(_.amount) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Ceiling(_.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: ceiling(_.amount) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Floor(_.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: floor(_.amount) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Round(_.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: round(_.amount) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Round(_.Amount, 1)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: round(_.amount, 1) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Truncate(_.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: truncate(_.amount) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Sign(_.Amount - 100m)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: sign(sub(_.amount, 100)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Sqrt((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: sqrt(toDouble(_.amount)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Pow((double) _.Amount, 2d)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: pow(toDouble(_.amount), 2) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Exp((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: exp(toDouble(_.amount)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Log((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: log(toDouble(_.amount)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Log((double) _.Amount, 10d)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: log(toDouble(_.amount), 10) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Log10((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: log10(toDouble(_.amount)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {A = Math.Sin((double) _.Amount), B = Math.Cos((double) _.Amount), C = Math.Tan((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ a: sin(toDouble(_.amount)), b: cos(toDouble(_.amount)), c: tan(toDouble(_.amount)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {A = Math.Asin((double) _.Amount), B = Math.Acos((double) _.Amount), C = Math.Atan((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ a: asin(toDouble(_.amount)), b: acos(toDouble(_.amount)), c: atan(toDouble(_.amount)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Math.Atan2((double) _.Amount, 2d)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: atan2(toDouble(_.amount), 2) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {A = Math.Max(_.Amount, 100m), B = Math.Min(_.Amount, 100m)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ a: max(_.amount, 100), b: min(_.amount, 100) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = double.DegreesToRadians((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: degreesToRadians(toDouble(_.amount)) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = double.RadiansToDegrees((double) _.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: radiansToDegrees(toDouble(_.amount)) })).toList(),

  // Conversions.
  [`_ => _.Source<Order>("Order").Select(_ => new {V = int.Parse(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toInt32(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = long.Parse(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toInt64(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = decimal.Parse(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toDecimal(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = double.Parse(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toDouble(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = short.Parse(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toInt16(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = byte.Parse(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toByte(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = float.Parse(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toSingle(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = bool.Parse(_.Audited)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toBoolean(_.audited) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Convert.ToInt32(_.Code)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toInt32(_.code) })).toList(),
  [`_ => _.Source<Order>("Order").Select(_ => new {V = Convert.ToString(_.Amount)}).ToListAsync()`]:
    () => scry.order.select(_ => ({ v: toText(_.amount) })).toList(),

  // Bytes.
  [`_ => _.Source<Shift>("Shift").Select(_ => new {V = _.Signature.Length}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ v: length(_.signature) })).toList(),
  [`_ => _.Source<Shift>("Shift").Select(_ => new {V = _.Signature.Contains((byte) 0x0B)}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ v: contains(_.signature, 0x0b) })).toList(),
  [`_ => _.Source<Shift>("Shift").Select(_ => new {V = _.Signature.ElementAt(1)}).ToListAsync()`]:
    () => scry.shift.select(_ => ({ v: elementAt(_.signature, 1) })).toList()
};
}

// What a TypeScript query cannot write, and why. Each is a C# spelling, not a request this client
// cannot make.
export const apart: Record<string, string> = {
  [`_ => _.Source<Employee>("Employee").Where(_ => _.Status != Status.FullTime || _.Name == "Bob").Select(_ => new NameRow(_.Name)).ToListAsync()`]:
    "C# compiles an enum literal compared with a member to its number, which travels as an Int32. A TypeScript query names the value, as C# does for the same value held in a variable.",
  [`_ => _.Source<Employee>("Employee", ["Name", "Status"]).ToListAsync()`]:
    "A C# source handed its own list of members to project. A TypeScript source projects its model's, which the default projection tests pin.",
  [`_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).FirstOrDefaultAsync(_ => _.Name == "Alice")`]:
    "A condition on a terminal after a projection, which the server refuses and the TypeScript types do not allow."
};

