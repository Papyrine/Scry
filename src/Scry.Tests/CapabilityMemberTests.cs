/// <summary>
/// A targeted command adds a <c>bool</c> to its target saying, row by row, whether the caller may send
/// it. To a client it is a member like any other — the default projection carries it, a query filters,
/// orders and groups by it — and the server computes it from the command's policy, in the database.
/// </summary>
[TestFixture]
public class CapabilityMemberTests
{
    // The model a generated client would hold for Contract: the capability is one more member.
    public class ContractModel
    {
        public int Id { get; init; }
        public string Name { get; init; } = null!;
        public bool CanSealContract { get; init; }
    }

    public class ShiftModel
    {
        public int Id { get; init; }
        public bool CanRenameShift { get; init; }
    }

    static string[] contractMembers = ["Id", "Name", "CanSealContract"];

    [Test]
    public async Task TheDefaultProjectionCarriesIt()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = await Client(context)
            .Source<ContractModel>("Contract", contractMembers)
            .OrderBy(_ => _.Id)
            .ToListAsync();

        // The sealed contract is refused by the policy's row condition; the others pass it.
        Assert.That(rows.Select(_ => (_.Id, _.CanSealContract)), Is.EqualTo([(1, true), (2, true), (UnsealedContractsPolicy.SealedId, false)]));
    }

    [Test]
    public async Task AWhereReadsIt()
    {
        await using var context = TestContext.CreateSeeded();

        var ids = await Client(context)
            .Source<ContractModel>("Contract", contractMembers)
            .Where(_ => _.CanSealContract)
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Id})
            .ToListAsync();

        Assert.That(ids.Select(_ => _.Id), Is.EqualTo([1, 2]));
    }

    [Test]
    public async Task AnOrderByReadsIt()
    {
        await using var context = TestContext.CreateSeeded();

        var ids = await Client(context)
            .Source<ContractModel>("Contract", contractMembers)
            .OrderBy(_ => _.CanSealContract)
            .ThenBy(_ => _.Id)
            .Select(_ => new {_.Id})
            .ToListAsync();

        Assert.That(ids.Select(_ => _.Id), Is.EqualTo([UnsealedContractsPolicy.SealedId, 1, 2]));
    }

    [Test]
    public async Task AGroupByReadsIt()
    {
        await using var context = TestContext.CreateSeeded();

        var groups = await Client(context)
            .Source<ContractModel>("Contract", contractMembers)
            .GroupBy(_ => _.CanSealContract)
            .Select(_ => new {_.Key, Count = _.Count()})
            .ToListAsync();

        Assert.That(groups.OrderBy(_ => _.Key).Select(_ => (_.Key, _.Count)), Is.EqualTo([(false, 1), (true, 2)]));
    }

    // A caller the policy refuses outright may send the command against no row, so every row says so —
    // and the row condition is never asked.
    [Test]
    public async Task ACommandWideDenialReadsFalseEverywhere()
    {
        await using var context = TestContext.CreateSeeded();
        await using var services = new ServiceCollection()
            .AddSingleton(new CommandGate {Open = false})
            .BuildServiceProvider();

        var rows = await Client(context, services)
            .Source<ContractModel>("Contract", contractMembers)
            .Select(_ => new {_.CanSealContract})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.CanSealContract), Is.EqualTo([false, false, false]));
    }

    // The row condition is the policy's, asked with this call's context: a caller whose desk holds one
    // contract may seal that one and no other, and a caller whose desk holds another reads the reverse —
    // on one processor, one after the other, so neither answer is the other caller's.
    [Test]
    public async Task EachCallerReadsTheirOwnRows()
    {
        await using var context = TestContext.CreateSeeded();
        await using var first = new ServiceCollection()
            .AddSingleton(new SealDesk {Contract = 1})
            .BuildServiceProvider();
        await using var second = new ServiceCollection()
            .AddSingleton(new SealDesk {Contract = 2})
            .BuildServiceProvider();

        var forFirst = await Capabilities(context, first);
        var forSecond = await Capabilities(context, second);

        Assert.Multiple(() =>
        {
            Assert.That(forFirst, Is.EqualTo([(1, true), (2, false), (UnsealedContractsPolicy.SealedId, false)]));
            Assert.That(forSecond, Is.EqualTo([(1, false), (2, true), (UnsealedContractsPolicy.SealedId, false)]));
        });
    }

    static async Task<List<(int Id, bool CanSeal)>> Capabilities(TestContext context, IServiceProvider services)
    {
        var rows = await Client(context, services)
            .Source<ContractModel>("Contract", contractMembers)
            .OrderBy(_ => _.Id)
            .Select(_ => new {_.Id, _.CanSealContract})
            .ToListAsync();
        return rows.Select(_ => (_.Id, _.CanSealContract)).ToList();
    }

    [Test]
    public async Task ACommandWithNoPolicyReadsTrue()
    {
        await using var context = TestContext.CreateSeeded();

        var rows = await Client(context)
            .Source<ShiftModel>("Shift", ["Id", "CanRenameShift"])
            .Select(_ => new {_.CanRenameShift})
            .ToListAsync();

        Assert.That(rows.Select(_ => _.CanRenameShift), Is.Not.Empty.And.All.True);
    }

    // The row condition is the policy's expression read against the row, in the statement — and the
    // literal it compares with is bound as a parameter rather than written into the SQL.
    [Test]
    public void TheRowConditionIsInTheStatementWithItsLiteralsBound()
    {
        using var context = TestContext.CreateSeeded();
        var request = Translator()
            .Source<ContractModel>("Contract", contractMembers)
            .Select(_ => new {_.CanSealContract})
            .ToScryRequest();

        var sql = commandsOn.ToQueryString(request, context, EmptyServices.Instance);

        Assert.Multiple(() =>
        {
            Assert.That(sql, Does.Contain("CASE"));
            Assert.That(sql, Does.Contain("DECLARE"));
            Assert.That(sql, Does.Not.Contain($"<> {UnsealedContractsPolicy.SealedId}"));
        });
    }

    [Test]
    public void ItIsNotTraversable()
    {
        using var context = TestContext.CreateSeeded();
        var request = QueryRequest.Create("Contract", [new WhereOp(new MemberNode(["CanSealContract", "Value"]))]);

        var exception = Assert.Throws<ScryValidationException>(() => SharedProcessor.Instance.Execute(request, context));

        Assert.That(exception!.Message, Does.Contain("Cannot traverse through non-navigation 'CanSealContract'"));
    }

    // Described as the plain bool it reads as, and never as part of a key, a sensitive member, or bytes.
    [Test]
    public void ItIsDescribedAsAnOrdinaryBool()
    {
        var contract = SharedProcessor.Instance.Describe().Types.Single(_ => _.Model == "ContractQueryModel");
        var capability = contract.Members.Single(_ => _.Name == "CanSealContract");

        Assert.Multiple(() =>
        {
            Assert.That(capability.TypeDisplay, Is.EqualTo("bool"));
            Assert.That(capability.IsCapability, Is.True);
            Assert.That(capability.Command, Is.EqualTo("SealContract"));
            Assert.That(capability.IsSensitive, Is.False);
            Assert.That(contract.Keys ?? [], Does.Not.Contain("CanSealContract"));
        });
    }

    // Declared by the target and inherited by what derives from it, as a member of the base would be.
    [Test]
    public void ADerivedTypeInheritsIt()
    {
        var types = SharedProcessor.Instance.Describe().Types;
        var signed = types.Single(_ => _.Model == "SignedContractQueryModel");

        Assert.Multiple(() =>
        {
            Assert.That(signed.Base, Is.EqualTo("ContractQueryModel"));
            Assert.That(signed.Members.Select(_ => _.Name), Does.Not.Contain("CanSealContract"));
        });
    }

    // Each run of a live query is a call of its own, so the capability is decided again on every run:
    // a write that changes what the row condition reads changes what the screen shows.
    [Test]
    public async Task ALiveQueryAnswersAgainWhenACapabilityFlips()
    {
        await using var database = await TestContext.CreateIsolated("CapabilityFlips");
        await using (var seeding = database.NewDbContext())
        {
            seeding.Contracts.Add(
                new()
                {
                    Id = 1,
                    Name = "Lease"
                });
            await seeding.SaveChangesAsync();
        }

        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.MaxPendingCommands = 10;
            options.MaxSubscriptions = 10;
            options.SubscriptionThrottle = TimeSpan.Zero;
            options.SubscriptionPollInterval = null;
        });
        await using var reading = database.NewDbContext();
        var client = new ScryClient(
            (request, _) => Task.FromResult(processor.Execute(request, reading)),
            subscribeTransport: (request, cancel) => processor.Subscribe(request, reading, cancel));

        await using var answers = client
            .Source<ContractModel>("Contract", contractMembers)
            .Where(_ => _.Id == 1)
            .Select(_ => new {_.CanSealContract})
            .Live()
            // ReSharper disable once MethodSupportsCancellation
            .GetAsyncEnumerator();

        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        Assert.That(answers.Current.Select(_ => _.CanSealContract), Is.EqualTo([true]));

        // Written through a context carrying the interceptor, which is what reports the save to the
        // live query. Spelled out rather than imported: EF's query extensions share names with Scry's.
        var options = Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions
            .UseSqlServer(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<TestContext>(), database.ConnectionString)
            .AddInterceptors(new ScryChangeInterceptor(processor.Changes))
            .Options;
        await using (var writing = new TestContext(options))
        {
            var contract = writing.Contracts.Single(_ => _.Id == 1);
            contract.Name = "";
            await writing.SaveChangesAsync();
        }

        Assert.That(await answers.MoveNextAsync().AsTask().WaitAsync(patience), Is.True);
        Assert.That(answers.Current.Select(_ => _.CanSealContract), Is.EqualTo([false]));
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static ScryClient Client(TestContext context, IServiceProvider? services = null) =>
        new((request, _) => Task.FromResult(commandsOn.Execute(request, context, services ?? EmptyServices.Instance)));

    // Commands on: a server with them off accepts none, and every capability reads false.
    static ScryProcessor commandsOn = ScryProcessor.Create<TestContext>(
        options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.MaxPendingCommands = 10;
        });

    [Test]
    public async Task WithCommandsOffNothingCanBeSent()
    {
        await using var context = TestContext.CreateSeeded();
        var client = new ScryClient((request, _) => Task.FromResult(SharedProcessor.Instance.Execute(request, context, EmptyServices.Instance)));

        var rows = await client
            .Source<ContractModel>("Contract", contractMembers)
            .ToListAsync();

        Assert.That(rows.Select(_ => _.CanSealContract), Is.All.False);
    }

    static ScryClient Translator() =>
        new((_, _) => throw new("These tests inspect the translated request; they do not send it."));

    sealed class EmptyServices :
        IServiceProvider
    {
        public static EmptyServices Instance { get; } = new();

        public object? GetService(Type serviceType) =>
            null;
    }
}
