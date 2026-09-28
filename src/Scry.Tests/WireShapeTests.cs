/// <summary>
/// What the translator actually puts on the wire, for every shape a client can write.
/// </summary>
/// <remarks>
/// <para>
/// The feature fixtures each run their LINQ end to end and assert on the rows that come back, which
/// leaves the request itself unpinned: a translator that emitted a different — but still valid — AST
/// for the same query would keep every one of them green, because the database answers both the same.
/// The wire is a hard compatibility contract, so the bytes are snapshotted here instead.
/// </para>
/// <para>
/// Every entry goes through a real terminal over a transport that records the request and stops
/// before sending, rather than through <c>ToScryRequest</c>: the terminal operators are part of what
/// travels, and a request captured at the transport is the request rather than a reconstruction of it.
/// </para>
/// <para>
/// <see cref="EveryOperatorAndNodeIsSnapshotted"/> and <see cref="EveryFunctionIsSnapshotted"/> are
/// what keep the corpus from going stale — a wire construct added with no shape written for it here
/// fails them, in the tree that can see the whole vocabulary.
/// </para>
/// </remarks>
public partial class WireShapeTests
{
    // ReSharper disable NotAccessedPositionalProperty.Local
    record NameRow(string Name);

    record OrderRow(string Region, decimal Amount);

    record LineRow(string Sku, int Quantity);

    record EmployeeCard(string Name, DepartmentCard Department);

    record DepartmentCard(string Name);

    // ReSharper restore NotAccessedPositionalProperty.Local

    [Test]
    public async Task Filtering() => await VerifyWire(await FilterShapes());

    static async Task<Entry[]> FilterShapes()
    {
        var wanted = Status.Contractor;
        var prefix = "A";
        string[] regions = ["North", "West"];
        var avatar = new byte[] {0x01, 0x02, 0x03};

        return
        [
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Active).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => !_.Active).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Status == wanted && _.Name.StartsWith(prefix)).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Status != Status.FullTime || _.Name == "Bob").Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.ManagerId == null).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.ManagerId.HasValue).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Avatar == avatar).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Address.Country == "UK").Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Manager!.Name == "Alice").Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Perks.HasFlag(Perks.Parking | Perks.Gym)).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => regions.Contains(_.Region)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => _.Amount >= 100m && _.Amount < 250m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => _.Amount - (_.Discount ?? 0m) > 90m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => _.Amount * 2m / 4m + 1m <= 100m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => _.Region.Length % 2 == 0).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => -_.Amount < 0m).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => _.Grade == 'A').Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => _.Quantity == 7u && _.Sku == ulong.MaxValue).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            await Wire(_ => _.Source<Order>("Order").Where(_ => _.Placed > new DateTime(2026, 1, 1)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
            // A comparison asking for a case sensitivity names the intent; the collation implementing
            // it is the server's, so no request can put a collation of its own choosing into the SQL.
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Name.Contains("LIC", StringComparison.OrdinalIgnoreCase)).Select(_ => new NameRow(_.Name)).ToListAsync()),
            await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.Name.Equals("Alice", StringComparison.Ordinal)).Select(_ => new NameRow(_.Name)).ToListAsync())
        ];
    }

    [Test]
    public async Task Ordering() => await VerifyWire(await OrderingShapes());

    static async Task<Entry[]> OrderingShapes() =>
    [
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderByDescending(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Status).ThenBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Status).ThenByDescending(_ => _.Name).Select(_ => new NameRow(_.Name)).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Reverse().Select(_ => new NameRow(_.Name)).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Skip(2).Take(1).Select(_ => new NameRow(_.Name)).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").OrderBy(_ => int.Parse(_.Code)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync())
    ];

    [Test]
    public async Task Projection() => await VerifyWire(await ProjectionShapes());

    static async Task<Entry[]> ProjectionShapes() =>
    [
        // No Select at all: the generated entry point's member list becomes an explicit projection, so
        // the response stays keyed by the names the client was generated with.
        await Wire(_ => _.Source<Employee>("Employee", ["Name", "Status"]).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new {_.Name, Manager = _.Manager!.Name}).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name))).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new EmployeeCard(_.Name, new(_.Department!.Name.ToUpper()))).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new {_.Name, _.Address.City, _.Address.Country}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new OrderRow(_.Region == "North" ? "N" : "S", _.Amount - (_.Discount ?? 0m))).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {Label = $"{_.Region}-{_.Quantity}"}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Region}).Distinct().ToListAsync())
    ];

    [Test]
    public async Task Grouping() => await VerifyWire(await GroupingShapes());

    static async Task<Entry[]> GroupingShapes() =>
    [
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Total = _.Sum(_ => _.Amount), Rows = _.Count()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Low = _.Min(_ => _.Amount), High = _.Max(_ => _.Amount), Mean = _.Average(o => o.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => new {_.Region, _.Grade}).Select(_ => new {_.Key.Region, _.Key.Grade, Total = _.Sum(_ => _.Amount)}).ToListAsync()),
        // A computed key has no member path to name it by, so the projection reads it as the query's
        // Nth key rather than as a member.
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Placed.DayOfWeek).Select(_ => new {Day = _.Key, Count = _.Count()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region.ToUpper()).Select(_ => new {Region = _.Key, Total = _.Sum(_ => _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region, (region, orders) => new {Region = region, Total = orders.Sum(_ => _.Amount)}).ToListAsync()),
        // A filter reading the group rather than the row is SQL's HAVING.
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Where(_ => _.Sum(o => o.Amount) > 100m && _.Key != "South").Select(_ => new {_.Key, Total = _.Sum(_ => _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Big = _.Count(_ => _.Amount > 90m), Graded = _.Count(_ => _.Grade == 'A')}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Grades = _.Select(_ => _.Grade).Distinct().Count()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").GroupBy(_ => _.Region).Select(_ => new {_.Key, Codes = string.Join(",", _.Select(_ => _.Code))}).ToListAsync())
    ];

    [Test]
    public async Task Joins() => await VerifyWire(await JoinShapes());

    static async Task<Entry[]> JoinShapes() =>
    [
        await Wire(client => client.Source<Employee>("Employee").Join(client.Source<Department>("Department"), _ => _.DepartmentId, _ => _.Id, (employee, department) => new {Employee = employee.Name, Department = department.Name}).ToListAsync()),
        await Wire(client => client.Source<Employee>("Employee").Join(client.Source<Department>("Department").Where(_ => _.Name == "Engineering"), _ => _.DepartmentId, _ => _.Id, (employee, department) => new {Employee = employee.Name, Department = department.Name}).ToListAsync()),
        await Wire(client => client.Source<Employee>("Employee").LeftJoin(client.Source<Department>("Department"), _ => _.DepartmentId, _ => _.Id, (employee, department) => new {Employee = employee.Name, Department = department!.Name}).ToListAsync()),
        await Wire(client => client.Source<Employee>("Employee").RightJoin(client.Source<Ticket>("Ticket"), _ => _.Id, _ => _.Id, (employee, ticket) => new {Employee = employee!.Name, Ticket = ticket.Name}).ToListAsync()),
        // A group join's inner side is a group rather than a row, so its members are folded rather
        // than read.
        await Wire(client => client.Source<Department>("Department").GroupJoin(client.Source<Employee>("Employee"), _ => _.Id, _ => _.DepartmentId, (department, employees) => new {Department = department.Name, Size = employees.Count()}).ToListAsync()),
        // A composite key matches part by part. It has no value of its own, so it is legal here and
        // nowhere else.
        await Wire(client => client.Source<Order>("Order").Join(client.Source<Order>("Order"), _ => new {_.Region, _.Grade}, _ => new {_.Region, _.Grade}, (outer, inner) => new {outer.Code, Matched = inner.Amount}).ToListAsync()),
        // An inner side carrying more than a filter travels as a pipeline of its own rather than as a
        // bare predicate.
        await Wire(client => client.Source<Order>("Order").Join(client.Source<Order>("Order").Where(_ => _.Grade == 'A').OrderByDescending(_ => _.Amount).Take(1), _ => _.Region, _ => _.Region, (outer, inner) => new {outer.Code, Matched = inner.Amount}).ToListAsync())
    ];

    [Test]
    public async Task SetOperations() => await VerifyWire(await SetShapes());

    static async Task<Entry[]> SetShapes() =>
    [
        await Wire(client => client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Union(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()),
        await Wire(client => client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Concat(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()),
        await Wire(client => client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Intersect(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()),
        await Wire(client => client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Except(client.Source<OrderLine>("OrderLine").Select(_ => new OrderRow(_.Sku, _.Price))).ToListAsync()),
        await Wire(client => client.Source<Order>("Order").Select(_ => new OrderRow(_.Region, _.Amount)).Union(client.Source<Order>("Order").Where(_ => _.Grade == 'A').OrderBy(_ => _.Amount).Take(2).Select(_ => new OrderRow(_.Region, _.Amount))).ToListAsync())
    ];

    [Test]
    public async Task Collections() => await VerifyWire(await CollectionShapes());

    static async Task<Entry[]> CollectionShapes() =>
    [
        // A flatten replaces the row every later operator reads; an aggregate folds the collection to
        // a scalar and leaves the row alone.
        await Wire(_ => _.Source<Order>("Order").SelectMany(_ => _.Lines).Select(_ => new LineRow(_.Sku, _.Quantity)).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Where(_ => _.Region == "North").SelectMany(_ => _.Lines).Where(_ => _.Quantity > 1).Select(_ => new LineRow(_.Sku, _.Quantity)).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Where(_ => _.Lines.Any(l => l.Price == 25m)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Where(_ => _.Lines.All(l => l.Quantity > 0)).Select(_ => new OrderRow(_.Region, _.Amount)).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Region, Lines = _.Lines.Count(l => l.Quantity > 1)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Region, Total = _.Lines.Sum(l => l.Price), Mean = _.Lines.Average(l => l.Price), Low = _.Lines.Min(l => l.Price), High = _.Lines.Max(l => l.Price)}).ToListAsync()),
        // A collection of values reads the element itself rather than a member of it.
        await Wire(_ => _.Source<Order>("Order").Where(_ => _.Tags.Contains("urgent")).Select(_ => new {_.Region}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Region, Total = _.Scores.Sum(), High = _.Scores.Max()}).ToListAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Where(_ => _.PreviousAddresses.Any(address => address.City == "Berlin")).Select(_ => new NameRow(_.Name)).ToListAsync()),
        // Membership of a set drawn from another source, which is resolved and policy-filtered before
        // the test.
        await Wire(client => client.Source<Employee>("Employee").Where(_ => client.Source<Department>("Department").Where(_ => _.Name == "Sales").Select(_ => _.Id).Contains(_.DepartmentId)).Select(_ => new NameRow(_.Name)).ToListAsync())
    ];

    [Test]
    public async Task Hierarchy() => await VerifyWire(await HierarchyShapes());

    static async Task<Entry[]> HierarchyShapes() =>
    [
        await Wire(_ => _.Source<Asset>("Asset").OfType<Vehicle>().Select(_ => new {_.Name, _.Wheels}).ToListAsync()),
        await Wire(_ => _.Source<Asset>("Asset").OfType<Building>().Where(_ => _.Floors > 1).Select(_ => new {_.Name, _.Floors}).ToListAsync())
    ];

    [Test]
    public async Task Terminals() => await VerifyWire(await TerminalShapes());

    static async Task<Entry[]> TerminalShapes() =>
    [
        await Wire(_ => _.Source<Employee>("Employee").CountAsync()),
        await Wire(_ => _.Source<Employee>("Employee").CountAsync(_ => _.Active)),
        await Wire(_ => _.Source<Employee>("Employee").LongCountAsync()),
        await Wire(_ => _.Source<Employee>("Employee").LongCountAsync(_ => _.Active)),
        await Wire(_ => _.Source<Employee>("Employee").AnyAsync()),
        await Wire(_ => _.Source<Employee>("Employee").AnyAsync(_ => _.Active)),
        await Wire(_ => _.Source<Employee>("Employee").AllAsync(_ => _.Active)),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).FirstAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).FirstOrDefaultAsync(_ => _.Name == "Alice")),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).SingleAsync()),
        await Wire(_ => _.Source<Employee>("Employee").Select(_ => new NameRow(_.Name)).SingleOrDefaultAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).LastAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).LastOrDefaultAsync()),
        // ElementAt is the Skip + First it abbreviates, and MaxBy the OrderBy + First.
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ElementAtAsync(1)),
        await Wire(_ => _.Source<Order>("Order").MaxByAsync(_ => _.Amount)),
        await Wire(_ => _.Source<Order>("Order").MinByAsync(_ => _.Amount)),
        await Wire(_ => _.Source<Order>("Order").SumAsync(_ => _.Amount)),
        await Wire(_ => _.Source<Order>("Order").AverageAsync(_ => _.Amount)),
        await Wire(_ => _.Source<Order>("Order").MinAsync(_ => _.Amount)),
        await Wire(_ => _.Source<Order>("Order").MaxAsync(_ => _.Amount)),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToPageAsync()),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToPageAsync(2)),
        await Wire(_ => _.Source<Employee>("Employee").OrderBy(_ => _.Name).Select(_ => new NameRow(_.Name)).ToPageAsync(2, "eyJrIjpbXX0.c2ln"))
    ];

    [Test]
    public async Task StringFunctions() => await VerifyWire(await StringShapes());

    static async Task<Entry[]> StringShapes() =>
    [
        await Wire(_ =>_.Source<Order>("Order").Select(_ => new {V = _.Region.Contains("or")}).ToListAsync()),
        await Wire(_ =>_.Source<Order>("Order").Select(_ => new {V = _.Region.StartsWith("No")}).ToListAsync()),
        await Wire(_ =>_.Source<Order>("Order").Select(_ => new {V = _.Region.EndsWith("th")}).ToListAsync()),
        // The char overloads reach the same wire functions as their string forms — a char constant
        // travels under the String tag — so what pins them is the constant rather than the function.
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {A = _.Region.StartsWith('N'), B = _.Region.EndsWith('h'), C = _.Region.Contains('o')}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.ToLower()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.ToUpper()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = string.IsNullOrEmpty(_.Region)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = string.IsNullOrWhiteSpace(_.Region)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Length}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Trim()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.TrimStart()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.TrimEnd()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Substring(1)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Substring(1, 2)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.IndexOf('o')}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.Replace("o", "0")}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.FirstOrDefault()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.LastOrDefault()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Amount.ToString()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Region.CompareTo("South")}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = string.Compare(_.Region, "South")}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Amount.CompareTo(100m)}).ToListAsync())
    ];

    [Test]
    public async Task TemporalFunctions() => await VerifyWire(await TemporalShapes());

    static async Task<Entry[]> TemporalShapes() =>
    [
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Placed.Year, _.Placed.Month, _.Placed.Day, _.Placed.DayOfYear}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Placed.Hour, _.Placed.Minute, _.Placed.Second, _.Placed.Millisecond}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Placed.Microsecond, _.Placed.Nanosecond}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {_.Placed.DayOfWeek, _.Placed.Date, _.Placed.TimeOfDay}).ToListAsync()),
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {_.Day.DayNumber}).ToListAsync()),
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {_.Duration.Hours, _.Duration.Minutes, _.Duration.Seconds}).ToListAsync()),
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {_.Duration.Milliseconds, _.Duration.Microseconds, _.Duration.Nanoseconds}).ToListAsync()),
        // Reading one temporal type as another, which the database performs — so the answer does not
        // depend on the client's calendar or its clock.
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {D = Date.FromDateTime(_.Placed), T = Time.FromDateTime(_.Placed)}).ToListAsync()),
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {T = Time.FromTimeSpan(_.Duration), Stamp = _.Day.ToDateTime(_.Start)}).ToListAsync()),
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {S = _.Stamped.ToUnixTimeSeconds(), Ms = _.Stamped.ToUnixTimeMilliseconds()}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {A = _.Placed.AddYears(1), B = _.Placed.AddMonths(1), C = _.Placed.AddDays(1), D = _.Placed.AddHours(1)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {A = _.Placed.AddMinutes(1), B = _.Placed.AddSeconds(1), C = _.Placed.AddMilliseconds(1)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = _.Placed.CompareTo(new(2026, 1, 1))}).ToListAsync())
    ];

    [Test]
    public async Task MathFunctions() => await VerifyWire(await MathShapes());

    static async Task<Entry[]> MathShapes() =>
    [
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Abs(_.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Ceiling(_.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Floor(_.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Round(_.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Round(_.Amount, 1)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Truncate(_.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Sign(_.Amount - 100m)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Sqrt((double) _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Pow((double) _.Amount, 2d)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Exp((double) _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Log((double) _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Log((double) _.Amount, 10d)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Log10((double) _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {A = Math.Sin((double) _.Amount), B = Math.Cos((double) _.Amount), C = Math.Tan((double) _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {A = Math.Asin((double) _.Amount), B = Math.Acos((double) _.Amount), C = Math.Atan((double) _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Math.Atan2((double) _.Amount, 2d)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {A = Math.Max(_.Amount, 100m), B = Math.Min(_.Amount, 100m)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = double.DegreesToRadians((double) _.Amount)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = double.RadiansToDegrees((double) _.Amount)}).ToListAsync())
    ];

    [Test]
    public async Task ConversionFunctions() => await VerifyWire(await ConversionShapes());

    static async Task<Entry[]> ConversionShapes() =>
    [
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = int.Parse(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = long.Parse(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = decimal.Parse(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = double.Parse(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = short.Parse(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = byte.Parse(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = float.Parse(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = bool.Parse(_.Audited)}).ToListAsync()),
        // The Convert spellings reach the same functions as Parse, and Convert.ToString is StringFrom
        // by another name.
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Convert.ToInt32(_.Code)}).ToListAsync()),
        await Wire(_ => _.Source<Order>("Order").Select(_ => new {V = Convert.ToString(_.Amount)}).ToListAsync())
    ];

    [Test]
    public async Task BinaryFunctions() => await VerifyWire(await BinaryShapes());

    static async Task<Entry[]> BinaryShapes() =>
    [
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {V = _.Signature.Length}).ToListAsync()),
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {V = _.Signature.Contains((byte) 0x0B)}).ToListAsync()),
        await Wire(_ => _.Source<Shift>("Shift").Select(_ => new {V = _.Signature.ElementAt(1)}).ToListAsync())
    ];

    /// <summary>
    /// The corpus above only guards what it covers. Every operator and every expression node the wire
    /// declares has to appear in a request some client LINQ here produced, or a construct added to the
    /// vocabulary would travel with nothing recording the bytes it travels as.
    /// </summary>
    [Test]
    public async Task EveryOperatorAndNodeIsSnapshotted()
    {
        var used = await Used(Discriminator());

        var missing = Declared(typeof(QueryOp))
            .Concat(Declared(typeof(Node)))
            .Where(_ => !used.Contains(_))
            .ToList();

        await Assert.That(missing).IsEmpty().Because($"The wire declares constructs no shape here carries, so what a client sends for them is unpinned: {string.Join(", ", missing)}");
    }

    /// <summary>
    /// The same guard over the callable set. <c>SupportedLinqTests</c> pins that every wire function is
    /// named by the analyzer's table; this pins that every one is also written as real client LINQ, so
    /// what a call travels as is recorded rather than only that it exists.
    /// </summary>
    [Test]
    public async Task EveryFunctionIsSnapshotted()
    {
        var used = await Used(Function());

        var missing = Enum.GetNames<KnownFunction>()
            .Where(_ => !used.Contains(_))
            .ToList();

        await Assert.That(missing).IsEmpty().Because($"KnownFunction has values no shape here carries, so what a client sends to call them is unpinned: {string.Join(", ", missing)}");
    }

    // Read back off the serialized corpus rather than off the request graph: what a snapshot pins is
    // the bytes, so what counts as covered is what reached them.
    static async Task<HashSet<string>> Used(Regex regex)
    {
        var json = string.Concat((await Corpus()).Select(_ => _.Wire));

        return regex
            .Matches(json)
            .Select(_ => _.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
    }

    static async Task<IEnumerable<Entry>> Corpus() =>
    [
        .. await FilterShapes(),
        .. await OrderingShapes(),
        .. await ProjectionShapes(),
        .. await GroupingShapes(),
        .. await JoinShapes(),
        .. await SetShapes(),
        .. await CollectionShapes(),
        .. await HierarchyShapes(),
        .. await TerminalShapes(),
        .. await StringShapes(),
        .. await TemporalShapes(),
        .. await MathShapes(),
        .. await ConversionShapes(),
        .. await BinaryShapes()
    ];

    static IEnumerable<string> Declared(Type wireBase) =>
        wireBase
            .GetCustomAttributes<JsonDerivedTypeAttribute>()
            .Select(_ => (string) _.TypeDiscriminator!);

    // ReSharper disable once NotAccessedPositionalProperty.Local
    record Entry(string Query, string Wire);

    /// <summary>
    /// Runs the query against a transport that records what it was handed and stops before sending, so
    /// what is snapshotted is a request that was on its way out rather than one rebuilt to look like it.
    /// </summary>
    static async Task<Entry> Wire(
        Func<ScryClient, Task> query,
        [CallerArgumentExpression(nameof(query))] string text = "")
    {
        QueryRequest? sent = null;
        var client = new ScryClient(
            (request, _) =>
            {
                sent = request;
                throw new StopBeforeSending();
            });

        try
        {
            query(client).GetAwaiter().GetResult();
        }
        catch (StopBeforeSending)
        {
        }

        await Assert.That(sent).IsNotNull().Because(text);

        return new(Label(text), JsonSerializer.Serialize(sent, indented));
    }

    /// <summary>
    /// The wire's own options, indented. What travels is compact — a request is bytes on a socket, not
    /// something anyone reads — but a compact request is one long line, and a diff over one long line
    /// says only that it changed. These files are read as the specification, so they are written to be
    /// read. Nothing but the whitespace between tokens differs: same resolver, same converters, so the
    /// member order and the escaping are the ones that travel.
    /// </summary>
    static JsonSerializerOptions indented = new(ScryJson.Options)
    {
        WriteIndented = true
    };

    // Written as one document rather than as a list of pairs: an indented request nested inside a
    // rendered object would be indented against the wrong margin, and the request is the thing being
    // read. Each entry is the query that produced it, then what it produced.
    static Task VerifyWire(Entry[] entries) =>
        Verify(string.Join("\n\n", entries.Select(_ => $"{_.Query}\n{_.Wire}")));

    // The query as it was written, minus the parameter the corpus threads the client through on and
    // the line breaks C# needed to fit it. Taken from the call site rather than restated as a string,
    // which is the one spelling that cannot drift from the code it labels.
    static string Label(string text)
    {
        var collapsed = Whitespace()
            .Replace(text, " ")
            .Trim();

        const string parameter = "client => ";
        if (collapsed.StartsWith(parameter, StringComparison.Ordinal))
        {
            return collapsed[parameter.Length..];
        }

        return collapsed;
    }

    sealed class StopBeforeSending :
        Exception;

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex("\"\\$type\":\\s*\"([^\"]+)\"")]
    private static partial Regex Discriminator();

    [GeneratedRegex("\"function\":\\s*\"([^\"]+)\"")]
    private static partial Regex Function();
}
