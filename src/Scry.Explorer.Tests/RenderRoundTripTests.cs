// Round-trip tests for ScryQueryRenderer: a wire request captured by the REAL client pipeline is
// rendered back to a snippet, the snippet is compiled and translated by the REAL explorer executor,
// and the two requests must serialize to identical JSON. The test models and the introspection
// describe the same shape, so the explorer-synthesized facade agrees with the local models on every
// name.
public class RenderRoundTripTests
{
    public enum Status
    {
        FullTime,
        PartTime,
        Contractor
    }

    public enum Perks
    {
        None,
        Parking,
        Gym
    }

    [ScryModel("Order", "Id", "Total", "CustomerName")]
    public class OrderQueryModel
    {
        public int Id { get; init; }
        public decimal Total { get; init; }
        public string CustomerName { get; init; } = null!;
    }

    [ScryModel("Department", "Id", "Name", "Code", "Active")]
    public class DepartmentQueryModel
    {
        public int Id { get; init; }
        public string Name { get; init; } = null!;
        public string Code { get; init; } = null!;
        public bool Active { get; init; }
    }

    [ScryModel(
        "Employee",
        "Id",
        "Name",
        "Age",
        "Salary",
        "Active",
        "Status",
        "Perks",
        "StartDate",
        "When",
        "Offset",
        "Duration",
        "Blob",
        "Rating",
        "Ssn",
        "Key",
        "DeptId")]
    public class EmployeeQueryModel
    {
        public int Id { get; init; }
        public string Name { get; init; } = null!;
        public int? Age { get; init; }
        public decimal Salary { get; init; }
        public bool Active { get; init; }
        public Status Status { get; init; }
        public Perks Perks { get; init; }
        public Date StartDate { get; init; }
        public DateTime? When { get; init; }
        public DateTimeOffset Offset { get; init; }
        public TimeSpan Duration { get; init; }
        public byte[] Blob { get; init; } = null!;
        public double Rating { get; init; }

        [ScrySensitive]
        public string Ssn { get; init; } = null!;

        public Guid Key { get; init; }
        public int DeptId { get; init; }
        public DepartmentQueryModel? Department { get; init; }
        public IReadOnlyList<OrderQueryModel> Orders { get; init; } = null!;
        public IReadOnlyList<string> Tags { get; init; } = null!;
    }

    [ScryModel(
        "Manager",
        "Id",
        "Name",
        "Age",
        "Salary",
        "Active",
        "Status",
        "Perks",
        "StartDate",
        "When",
        "Offset",
        "Duration",
        "Blob",
        "Rating",
        "Ssn",
        "Key",
        "DeptId",
        "Reports")]
    public class ManagerQueryModel :
        EmployeeQueryModel
    {
        public int Reports { get; init; }
    }

    // The same shape, described the way the server's introspection endpoint would — what the
    // explorer synthesizes its facade from.
    static ScryIntrospection introspection = new(
        ScryIntrospection.CurrentVersion,
        MaxPageSize: 200,
        Sources:
        [
            new("Employee", "EfCore", "EmployeeQueryModel"),
            new("Manager", "EfCore", "ManagerQueryModel"),
            new("Order", "EfCore", "OrderQueryModel"),
            new("Department", "EfCore", "DepartmentQueryModel")
        ],
        Types:
        [
            new("EmployeeQueryModel",
            [
                new("Id", "int", NeedsNullDefault: false, IsNavigation: false),
                new("Name", "string", NeedsNullDefault: true, IsNavigation: false),
                new("Age", "int?", NeedsNullDefault: false, IsNavigation: false),
                new("Salary", "decimal", NeedsNullDefault: false, IsNavigation: false),
                new("Active", "bool", NeedsNullDefault: false, IsNavigation: false),
                new("Status", "Status", NeedsNullDefault: false, IsNavigation: false),
                new("Perks", "Perks", NeedsNullDefault: false, IsNavigation: false),
                new("StartDate", "global::System.DateOnly", NeedsNullDefault: false, IsNavigation: false),
                new("When", "global::System.DateTime?", NeedsNullDefault: false, IsNavigation: false),
                new("Offset", "global::System.DateTimeOffset", NeedsNullDefault: false, IsNavigation: false),
                new("Duration", "global::System.TimeSpan", NeedsNullDefault: false, IsNavigation: false),
                new("Blob", "byte[]", NeedsNullDefault: true, IsNavigation: false),
                new("Rating", "double", NeedsNullDefault: false, IsNavigation: false),
                new("Ssn", "string", NeedsNullDefault: true, IsNavigation: false)
                {
                    IsSensitive = true
                },
                new("Key", "global::System.Guid", NeedsNullDefault: false, IsNavigation: false),
                new("DeptId", "int", NeedsNullDefault: false, IsNavigation: false),
                new("Department", "DepartmentQueryModel?", NeedsNullDefault: false, IsNavigation: true),
                new("Orders", "global::System.Collections.Generic.IReadOnlyList<OrderQueryModel>", NeedsNullDefault: true, IsNavigation: false, IsCollection: true),
                new("Tags", "global::System.Collections.Generic.IReadOnlyList<string>", NeedsNullDefault: true, IsNavigation: false, IsCollection: true)
            ]),
            new("ManagerQueryModel",
            [
                new("Reports", "int", NeedsNullDefault: false, IsNavigation: false)
            ])
            {
                Base = "EmployeeQueryModel"
            },
            new("OrderQueryModel",
            [
                new("Id", "int", NeedsNullDefault: false, IsNavigation: false),
                new("Total", "decimal", NeedsNullDefault: false, IsNavigation: false),
                new("CustomerName", "string", NeedsNullDefault: true, IsNavigation: false)
            ]),
            new("DepartmentQueryModel",
            [
                new("Id", "int", NeedsNullDefault: false, IsNavigation: false),
                new("Name", "string", NeedsNullDefault: true, IsNavigation: false),
                new("Code", "string", NeedsNullDefault: true, IsNavigation: false),
                new("Active", "bool", NeedsNullDefault: false, IsNavigation: false)
            ])
        ],
        Enums:
        [
            new("Status", ["FullTime", "PartTime", "Contractor"]),
            new("Perks", ["None", "Parking", "Gym"])
        ])
    {
        SchemaStamp = "render-round-trip"
    };

    static IReadOnlyList<MetadataReference> scryReferences =
    [
        MetadataReference.CreateFromFile(typeof(ScryClient).Assembly.Location),
        MetadataReference.CreateFromFile(typeof(QueryRequest).Assembly.Location)
    ];

    static SnippetExecutor executor = SnippetExecutor.Create(introspection, scryReferences);

    // The capture-only client the corpus is built with. The stamp matches the introspection's, so
    // both sides of a round trip stamp their requests identically.
    static ScryClient client = new((_, _) => Task.FromResult<QueryResponse>(null!))
    {
        SchemaStamp = "render-round-trip"
    };

    static IQueryable<T> Source<T>(string name) =>
        client.Source<T>(name, typeof(T).GetCustomAttribute<ScryModelAttribute>()!.Members);

    static IQueryable<EmployeeQueryModel> Employee => Source<EmployeeQueryModel>("Employee");

    static IQueryable<DepartmentQueryModel> Department => Source<DepartmentQueryModel>("Department");

    static IQueryable<OrderQueryModel> Order => Source<OrderQueryModel>("Order");

    [Before(Class)]
    public static void RegisterSources()
    {
        // Touch every source once, so the renderer's model registry can resolve names — an OfType
        // target included — whichever test runs first.
        _ = Employee;
        _ = Department;
        _ = Order;
        _ = Source<ManagerQueryModel>("Manager");
    }

    static async Task<string> RoundTrip(QueryRequest request)
    {
        await Assert.That(ScryQueryRenderer.TryRender(request, out var code, out var refusal)).IsTrue().Because($"render refused: {refusal}");
        var translated = executor.Translate(code!);
        await Assert.That(ScryJson.Serialize(translated)).IsEqualTo(ScryJson.Serialize(request)).Because(code!);
        return code!;
    }

    static async Task AssertRefused(QueryRequest request, RenderRefusal expected)
    {
        await Assert.That(ScryQueryRenderer.TryRender(request, out var code, out var refusal)).IsFalse().Because(code!);
        await Assert.That(refusal).IsEqualTo(expected);
    }

    [Test]
    public async Task WhereOmitsDefaultProjection()
    {
        var code = await RoundTrip(Employee.Where(_ => _.Active).ToScryRequest());
        await Assert.That(code).DoesNotContain("Select");
    }

    [Test]
    public async Task OrderingAndPaging() =>
        await RoundTrip(
            Employee
                .OrderBy(_ => _.Name)
                .ThenByDescending(_ => _.Salary)
                .Skip(10)
                .Take(5)
                .ToScryRequest());

    [Test]
    public async Task ExplicitSelect() =>
        await RoundTrip(Employee.Select(_ => new {_.Name, _.Salary}).ToScryRequest());

    [Test]
    public async Task SelectWithRenamedMember() =>
        await RoundTrip(Employee.Select(_ => new {Label = _.Name, _.Active}).ToScryRequest());

    [Test]
    public async Task SelectNestedNavigation() =>
        await RoundTrip(
            Employee
                .Select(_ => new {_.Name, Dept = new {_.Department!.Code, City = _.Department.Name}})
                .ToScryRequest());

    [Test]
    public async Task DistinctAfterSelect() =>
        await RoundTrip(Employee.Select(_ => new {_.Name}).Distinct().ToScryRequest());

    [Test]
    public async Task ReverseAfterOrdering() =>
        await RoundTrip(Employee.OrderBy(_ => _.Name).Reverse().ToScryRequest());

    [Test]
    public async Task OfTypeUsesDerivedDefaultProjection()
    {
        var code = await RoundTrip(Employee.OfType<ManagerQueryModel>().ToScryRequest());
        await Assert.That(code).Contains(".OfType<ManagerQueryModel>()");
        await Assert.That(code).DoesNotContain("Select");
    }

    [Test]
    public async Task SelectManyUsesElementDefaultProjection()
    {
        var code = await RoundTrip(Employee.SelectMany(_ => _.Orders).ToScryRequest());
        await Assert.That(code).DoesNotContain("Select(");
    }

    [Test]
    public async Task SelectManyWithExplicitSelect() =>
        await RoundTrip(Employee.SelectMany(_ => _.Orders).Select(_ => new {_.Total}).ToScryRequest());

    // Every renderable terminal, spelled back and folded to the identical wire op.
    [Test]
    public async Task TerminalToList() =>
        await RoundTrip(Employee.Where(_ => _.Active).ToScryRequest());

    [Test]
    public async Task TerminalCount() =>
        await RoundTrip(Employee.Where(_ => _.Active).ToScryRequest(new CountOp()));

    [Test]
    public async Task TerminalAny() =>
        await RoundTrip(Employee.Where(_ => _.Active).ToScryRequest(new AnyOp()));

    [Test]
    public async Task TerminalFirst() =>
        await RoundTrip(Employee.OrderBy(_ => _.Name).ToScryRequest(new FirstOp(false)));

    [Test]
    public async Task TerminalFirstOrDefault() =>
        await RoundTrip(Employee.OrderBy(_ => _.Name).ToScryRequest(new FirstOp(true)));

    [Test]
    public async Task TerminalSingle() =>
        await RoundTrip(Employee.Where(_ => _.Id == 1).ToScryRequest(new SingleOp(false)));

    [Test]
    public async Task TerminalSingleOrDefault() =>
        await RoundTrip(Employee.Where(_ => _.Id == 1).ToScryRequest(new SingleOp(true)));

    static Node NamePredicate() =>
        new BinaryNode(BinaryOp.Equal, new MemberNode(["Name"]), new ConstNode("x", ClrTypeTag.String));

    [Test]
    public Task RefusesPredicateFirst() =>
        AssertRefused(Employee.ToScryRequest(new FirstOp(false, NamePredicate())), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesPredicateSingle() =>
        AssertRefused(Employee.ToScryRequest(new SingleOp(false, NamePredicate())), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesPredicateCount() =>
        AssertRefused(Employee.ToScryRequest(new CountOp(NamePredicate())), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesPredicateAny() =>
        AssertRefused(Employee.ToScryRequest(new AnyOp(NamePredicate())), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesAll() =>
        AssertRefused(Employee.ToScryRequest(new AllOp(NamePredicate())), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesLast() =>
        AssertRefused(Employee.OrderBy(_ => _.Name).ToScryRequest(new LastOp(false)), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesLongCount() =>
        AssertRefused(Employee.ToScryRequest(new LongCountOp()), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesAggregateTerminal() =>
        AssertRefused(Employee.ToScryRequest(new AggregateOp(AggregateFn.Sum, new MemberNode(["Salary"]))), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesPage() =>
        AssertRefused(Employee.ToScryRequest(new PageOp(10)), RenderRefusal.UnsupportedTerminal);

    [Test]
    public Task RefusesSensitiveConstant() =>
        AssertRefused(Employee.Where(_ => _.Ssn == "123-45-6789").ToScryRequest(), RenderRefusal.SensitiveConstants);

    [Test]
    public async Task RendersSensitiveOrderingOnly() =>
        await RoundTrip(Employee.OrderBy(_ => _.Ssn).ToScryRequest());

    [Test]
    public Task RefusesEnumConstantOnUnregisteredSource() =>
        AssertRefused(
            new(
                1,
                "Ghost",
                [
                    new WhereOp(
                        new BinaryNode(
                            BinaryOp.Equal,
                            new MemberNode(["Kind"]),
                            new ConstNode("A", ClrTypeTag.Enum)))
                ]),
            RenderRefusal.UnresolvedModel);

    // Literal edge cases.
    [Test]
    public async Task StringEscapes() =>
        await RoundTrip(Employee.Where(_ => _.Name == "he said \"hi\" \\ twice\nover\théllo→").ToScryRequest());

    [Test]
    public async Task NullComparison() =>
        await RoundTrip(Employee.Where(_ => _.Age == null).ToScryRequest());

    [Test]
    public async Task HasValueComparison() =>
        await RoundTrip(Employee.Where(_ => _.When.HasValue).ToScryRequest());

    [Test]
    public async Task NegativeNumbers() =>
        await RoundTrip(Employee.Where(_ => _.Age > -5 && _.Salary > -10.5m).ToScryRequest());

    [Test]
    public async Task LongAndDoubleSuffixes() =>
        await RoundTrip(Employee.Where(_ => _.Offset.ToUnixTimeSeconds() > 100L && _.Rating >= 100d).ToScryRequest());

    [Test]
    public async Task DoubleSpecials() =>
        await RoundTrip(Employee.Where(_ => _.Rating == double.NaN || _.Rating == double.PositiveInfinity || _.Rating > 4.5).ToScryRequest());

    [Test]
    public async Task UtcDateTimeConstant() =>
        await RoundTrip(Employee.Where(_ => _.When > new DateTime(2026, 5, 1, 12, 30, 0, DateTimeKind.Utc)).ToScryRequest());

    [Test]
    public async Task DateOnlyConstant() =>
        await RoundTrip(Employee.Where(_ => _.StartDate >= new Date(2026, 1, 15)).ToScryRequest());

    [Test]
    public async Task GuidConstant() =>
        await RoundTrip(Employee.Where(_ => _.Key == Guid.Parse("11111111-2222-3333-4444-555555555555")).ToScryRequest());

    [Test]
    public async Task BytesConstant() =>
        await RoundTrip(Employee.Where(_ => _.Blob == new byte[] {1, 2, 250}).ToScryRequest());

    [Test]
    public async Task TimeSpanConstant() =>
        await RoundTrip(Employee.Where(_ => _.Duration > TimeSpan.FromMinutes(90)).ToScryRequest());

    [Test]
    public async Task DateTimeOffsetConstant() =>
        await RoundTrip(Employee.Where(_ => _.Offset > new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero)).ToScryRequest());

    // The sub-second part and the offset are both on the wire, so the constructed value the renderer
    // spells has to reproduce the text exactly — a default spelling on either side drops one of them
    // and the render is refused instead.
    [Test]
    public async Task SubSecondDateTimeOffsetConstant() =>
        await RoundTrip(Employee.Where(_ => _.Offset > new DateTimeOffset(2026, 3, 4, 5, 6, 7, 123, TimeSpan.FromHours(2))).ToScryRequest());

    // A time of day reaches the renderer as the argument that composes a timestamp, and carries its
    // seconds there for the same reason.
    [Test]
    public async Task TimeOfDayConstant() =>
        await RoundTrip(Employee.Where(_ => _.StartDate.ToDateTime(new(5, 6, 7, 123)) > new DateTime(2026, 1, 1)).ToScryRequest());

    [Test]
    public async Task EnumConstant() =>
        await RoundTrip(Employee.Where(_ => _.Status == Status.PartTime).ToScryRequest());

    [Test]
    public async Task UndefinedEnumConstant() =>
        await RoundTrip(Employee.Where(_ => _.Status == (Status) 7).ToScryRequest());

    [Test]
    public async Task EnumHasFlag() =>
        await RoundTrip(Employee.Where(_ => _.Perks.HasFlag(Perks.Gym)).ToScryRequest());

    [Test]
    public async Task DayOfWeekComparison() =>
        await RoundTrip(Employee.Where(_ => _.When!.Value.DayOfWeek == DayOfWeek.Monday).ToScryRequest());

    // String functions.
    [Test]
    public async Task StringFunctions() =>
        await RoundTrip(
            Employee
                .Where(_ => _.Name.Contains("xx") && _.Name.StartsWith("aa") && _.Name.EndsWith("zz"))
                .Where(_ => _.Name.ToLower().Trim().Length > 2)
                .Where(_ => _.Name.Substring(1, 2).IndexOf("qq") < 0)
                .Where(_ => _.Name.Replace("a", "b") != "c")
                .ToScryRequest());

    [Test]
    public async Task StringStatics() =>
        await RoundTrip(Employee.Where(_ => !string.IsNullOrEmpty(_.Name) && !string.IsNullOrWhiteSpace(_.Name)).ToScryRequest());

    [Test]
    public async Task CollatedComparisons() =>
        await RoundTrip(
            Employee
                .Where(_ => _.Name.Equals("x", StringComparison.OrdinalIgnoreCase))
                .Where(_ => _.Name.StartsWith("yy", StringComparison.Ordinal))
                .ToScryRequest());

    [Test]
    public async Task StringFirstAsCharComparison() =>
        await RoundTrip(Employee.Where(_ => _.Name.FirstOrDefault() == 'x' && _.Name.LastOrDefault() == 'z').ToScryRequest());

    [Test]
    public async Task StringConcatChain() =>
        await RoundTrip(Employee.Select(_ => new {Label = _.Name + "-" + _.Id}).ToScryRequest());

    [Test]
    public async Task ToStringOnNullableMember() =>
        await RoundTrip(Employee.Where(_ => _.Age.ToString() == "30").ToScryRequest());

    [Test]
    public async Task CompareTo() =>
        await RoundTrip(Employee.Where(_ => _.Name.CompareTo("m") > 0).ToScryRequest());

    [Test]
    public async Task NumericParse() =>
        await RoundTrip(Employee.Where(_ => int.Parse(_.Name) > 5 && double.Parse(_.Name) < 9.5).ToScryRequest());

    // Dates and times.
    [Test]
    public async Task DateParts() =>
        await RoundTrip(
            Employee
                .Where(_ => _.StartDate.Year == 2026 && _.When!.Value.Month == 5 && _.When.Value.DayOfYear > 100)
                .ToScryRequest());

    [Test]
    public async Task DateAdds() =>
        await RoundTrip(Employee.Where(_ => _.When!.Value.AddDays(1).AddYears(2) > new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)).ToScryRequest());

    [Test]
    public async Task TimeOfDayComparison() =>
        await RoundTrip(Employee.Where(_ => _.When!.Value.TimeOfDay > TimeSpan.FromHours(9)).ToScryRequest());

    [Test]
    public async Task TimeSpanParts() =>
        await RoundTrip(Employee.Where(_ => _.Duration.Hours > 1 && _.Duration.Minutes < 30).ToScryRequest());

    [Test]
    public async Task DateOnlyFromDateTime() =>
        await RoundTrip(Employee.Where(_ => Date.FromDateTime(_.When!.Value) == new Date(2026, 2, 3)).ToScryRequest());

    // Math.
    [Test]
    public async Task MathFunctions() =>
        await RoundTrip(
            Employee
                .Where(_ => Math.Abs(_.Rating) > 1 && Math.Round(_.Rating, 1) < 5 && Math.Pow(_.Rating, 2d) > 4)
                .ToScryRequest());

    [Test]
    public async Task AngleConversions() =>
        await RoundTrip(Employee.Where(_ => double.DegreesToRadians(_.Rating) > 1).ToScryRequest());

    // Bytes.
    [Test]
    public async Task ByteFunctions() =>
        await RoundTrip(
            Employee
                .Where(_ => _.Blob.Length > 3 && _.Blob.Contains((byte) 7) && _.Blob.ElementAt(1) == 9)
                .ToScryRequest());

    // Sets and membership.
    [Test]
    public async Task InIntegers()
    {
        var ids = new[] {1, 2, 3};
        await RoundTrip(Employee.Where(_ => ids.Contains(_.Id)).ToScryRequest());
    }

    [Test]
    public async Task InEnums()
    {
        // A List rather than an array: an enum array's Contains binds a MemoryExtensions
        // overload the translator refuses, in the corpus exactly as in a rendered snippet.
        var statuses = new List<Status> {Status.FullTime, Status.Contractor};
        await RoundTrip(Employee.Where(_ => statuses.Contains(_.Status)).ToScryRequest());
    }

    [Test]
    public async Task InStrings()
    {
        var names = new[] {"a", "b"};
        await RoundTrip(Employee.Where(_ => names.Contains(_.Name)).ToScryRequest());
    }

    [Test]
    public async Task InEmptySet()
    {
        var ids = Array.Empty<int>();
        await RoundTrip(Employee.Where(_ => ids.Contains(_.Id)).ToScryRequest());
    }

    [Test]
    public async Task InSource()
    {
        var departments = Department;
        await RoundTrip(Employee.Where(_ => departments.Select(_ => _.Name).Contains(_.Name)).ToScryRequest());
    }

    [Test]
    public async Task InSourceWithFilter()
    {
        var departments = Department;
        await RoundTrip(
            Employee
                .Where(_ => departments.Where(_ => _.Active).Select(_ => _.Id).Contains(_.DeptId))
                .ToScryRequest());
    }

    // Subqueries over collection navigations.
    [Test]
    public async Task SubqueryAnyWithPredicate() =>
        await RoundTrip(Employee.Where(_ => _.Orders.Any(_ => _.Total > 10)).ToScryRequest());

    [Test]
    public async Task SubqueryCountProperty() =>
        await RoundTrip(Employee.Where(_ => _.Orders.Count > 2).ToScryRequest());

    [Test]
    public async Task SubquerySumSelector() =>
        await RoundTrip(Employee.Where(_ => _.Orders.Sum(_ => _.Total) > 100).ToScryRequest());

    [Test]
    public async Task SubqueryFilteredFold() =>
        await RoundTrip(Employee.Where(_ => _.Orders.Where(_ => _.Total > 1).Max(_ => _.Total) > 50).ToScryRequest());

    [Test]
    public async Task SubqueryContainsOverValues() =>
        await RoundTrip(Employee.Where(_ => _.Tags.Contains("urgent")).ToScryRequest());

    // Conditionals and arithmetic.
    [Test]
    public async Task ConditionalProjection() =>
        await RoundTrip(Employee.Select(_ => new {_.Name, Band = _.Age > 30 ? "old" : "young"}).ToScryRequest());

    [Test]
    public async Task CoalesceAndArithmetic() =>
        await RoundTrip(Employee.Where(_ => (_.Age ?? 0) * 2 + 1 > 19 && _.Salary % 2 == 0).ToScryRequest());

    [Test]
    public async Task UnaryOperators() =>
        await RoundTrip(Employee.Where(_ => !_.Active || -_.Salary < -10m).ToScryRequest());

    // Grouping.
    [Test]
    public async Task GroupBySingleKey() =>
        await RoundTrip(
            Employee
                .GroupBy(_ => _.Status)
                .Select(g => new {g.Key, Count = g.Count(), Total = g.Sum(_ => _.Salary)})
                .ToScryRequest());

    [Test]
    public async Task GroupByCompositeKey() =>
        await RoundTrip(
            Employee
                .GroupBy(_ => new {_.Status, _.Active})
                .Select(g => new {g.Key.Status, g.Key.Active, Count = g.Count()})
                .ToScryRequest());

    [Test]
    public async Task GroupByComputedKeyPart() =>
        await RoundTrip(
            Employee
                .GroupBy(_ => new {_.Status, Key1 = _.Name.ToUpper()})
                .Select(g => new {g.Key.Status, Up = g.Key.Key1, Count = g.Count()})
                .ToScryRequest());

    [Test]
    public async Task GroupByComputedSingleKey() =>
        await RoundTrip(
            Employee
                .GroupBy(_ => _.Name.ToUpper())
                .Select(g => new {g.Key, Count = g.Count()})
                .ToScryRequest());

    [Test]
    public async Task GroupByHaving() =>
        await RoundTrip(
            Employee
                .GroupBy(_ => _.Status)
                .Where(g => g.Count() > 1)
                .Select(g => new {g.Key, Total = g.Sum(_ => _.Salary)})
                .ToScryRequest());

    [Test]
    public async Task GroupByAggregateForms() =>
        await RoundTrip(
            Employee
                .GroupBy(_ => _.Status)
                .Select(g => new
                {
                    g.Key,
                    Actives = g.Count(_ => _.Active),
                    DistinctSalaries = g.Select(_ => _.Salary).Distinct().Count(),
                    DistinctTotal = g.Select(_ => _.Salary).Distinct().Sum(),
                    Filtered = g.Where(_ => _.Active).Sum(_ => _.Salary),
                    Names = string.Join(", ", g.Select(_ => _.Name))
                })
                .ToScryRequest());

    // Joins.
    [Test]
    public async Task InnerJoinWithInnerPredicate() =>
        await RoundTrip(
            Employee
                .Join(
                    Department.Where(d => d.Active),
                    _ => _.DeptId,
                    d => d.Id,
                    (e, d) => new {e.Name, Dept = d.Name})
                .ToScryRequest());

    [Test]
    public async Task LeftJoin() =>
        await RoundTrip(
            Employee
                .LeftJoin(
                    Department,
                    _ => _.DeptId,
                    d => d.Id,
                    (e, d) => new {e.Name, Dept = d!.Name})
                .ToScryRequest());

    [Test]
    public async Task CompositeKeyJoin() =>
        await RoundTrip(
            Employee
                .Join(
                    Department,
                    _ => new {A = _.DeptId, B = _.Name},
                    d => new {A = d.Id, B = d.Name},
                    (e, d) => new {e.Salary, d.Code})
                .ToScryRequest());

    [Test]
    public async Task GroupJoinWithAggregates() =>
        await RoundTrip(
            Employee
                .GroupJoin(
                    Order,
                    _ => _.Id,
                    _ => _.Id,
                    // ReSharper disable PossibleMultipleEnumeration
                    (e, g) => new {e.Name, Total = g.Sum(_ => _.Total), Count = g.Count()})
                // ReSharper restore PossibleMultipleEnumeration
                .ToScryRequest());

    [Test]
    public async Task JoinWithInnerOps() =>
        await RoundTrip(
            Employee
                .Join(
                    Department.OrderBy(d => d.Name).Take(5),
                    _ => _.DeptId,
                    d => d.Id,
                    (e, d) => new {e.Name, Dept = d.Name})
                .ToScryRequest());

    // Set operators.
    [Test]
    public async Task UnionWithPredicateOperand() =>
        await RoundTrip(
            Employee
                .Where(_ => _.Active)
                .Select(_ => new {_.Name})
                .Union(Employee.Where(_ => _.Age > 30).Select(_ => new {_.Name}))
                .ToScryRequest());

    [Test]
    public async Task ConcatWithOperandOps() =>
        await RoundTrip(
            Employee
                .Select(_ => new {_.Name})
                .Concat(Employee.OrderBy(_ => _.Name).Take(3).Select(_ => new {_.Name}))
                .ToScryRequest());

    [Test]
    public async Task ExceptOperand() =>
        await RoundTrip(
            Employee
                .Select(_ => new {_.Name})
                .Except(Employee.Select(x => new {x.Name}))
                .ToScryRequest());
}
