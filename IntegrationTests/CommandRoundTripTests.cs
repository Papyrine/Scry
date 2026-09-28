using Sample.CommandHandlers;
// UseSqlServer only — importing the whole Microsoft.EntityFrameworkCore namespace would pull in EF
// Core's own IQueryable extensions and collide with the Scry client terminals.
using static Microsoft.EntityFrameworkCore.SqlServerDbContextOptionsExtensions;
// These drive a live query's enumerator by hand and end it by disposing it.
// ReSharper disable MethodSupportsCancellation

/// <summary>
/// Every generated command, sent over HTTP to a host running the sample's own handlers: what
/// <see cref="HttpRoundTripTests"/> does for queries. The client is generated from the sample model's
/// DLL and the server binds into the model's own classes, so what has to hold is that the two agree —
/// on names, on keys, on the payload's shape and its enums, and on what comes back.
/// </summary>
[NotInParallel]
[DependsOn<HttpRoundTripTests.StaleClient>(nameof(HttpRoundTripTests.StaleClient.AClientThatDoesNotKnowRetriesInABody), ProceedOnFailure = true)]
public class CommandRoundTripTests
{
    static readonly SqlInstance<Sample.Model.SampleContext> sqlInstance = new(
        constructInstance: _ => new(_.Options),
        buildTemplate: _ =>
        {
            Sample.Model.SampleContext.Initialize(_);
            return Task.CompletedTask;
        });

    static WebApplication app = null!;
    static SqlDatabase<Sample.Model.SampleContext> database = null!;
    static ScryClient client = null!;
    static ScryQuery query = null!;

    [Before(Class)]
    public static async Task StartServer()
    {
        database = await sqlInstance.Build();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddDbContext<Sample.Model.SampleContext>(
            (services, options) => options
                .UseSqlServer(database.ConnectionString)
                .AddInterceptors(services.GetRequiredService<ScryChangeInterceptor>()));
        builder.Services.AddSampleCommandHandlers();
        builder.Services.Configure<SampleCommandOptions>(_ => _.SlowDelay = TimeSpan.FromMilliseconds(1500));
        builder.Services.AddScry<Sample.Model.SampleContext>(options =>
        {
            options.AddPocoSource(_ => Sample.Model.Holiday.Seed());
            options.AddAttachmentPolicy<Sample.Model.Department, AllowAttachmentPolicy>();
            options.AddAttachmentPolicy<Sample.Model.Employee, AllowPhotoAttachmentPolicy>();
            options.MaxSubscriptions = 10;
            options.SubscriptionThrottle = TimeSpan.Zero;
            options.SubscriptionPollInterval = null;
            options.UseSampleCommands();
        });

        app = builder.Build();
        app.MapScry("/api/query");
        await app.StartAsync();

        client = ScryClient.ForHttp(app.GetTestClient(), "/api/query");
        query = new(client);
    }

    [After(Class)]
    public static async Task StopServer()
    {
        await client.DisposeAsync();
        await app.StopAsync();
        await app.DisposeAsync();
        await database.DisposeAsync();
    }

    [Test]
    public async Task ACreateAnswersWithItsTypedResult()
    {
        var outcome = await query.Commands.CreateEmployee(new() {Name = "Dana", DepartmentId = 1, Status = Status.Contractor});

        var id = outcome.EnsureCompleted().Value.Id;
        var hired = await query.Employee
            .Where(_ => _.Id == id)
            .Select(_ => new {_.Name, _.Status, _.Active})
            .SingleAsync();
        await Assert.That(hired).IsNotNull();

        using (Assert.Multiple())
        {
            await Assert.That(hired!.Name).IsEqualTo("Dana");
            await Assert.That(hired.Status).IsEqualTo(Status.Contractor);
            await Assert.That(hired.Active).IsTrue();
        }
    }

    // The payload's enum goes by name, as a query constant's does, and binds into the model's own enum.
    [Test]
    public async Task AnEnumTravelsByName()
    {
        CommandRequest? sent = null;
        void Watch(ScryCommandActivity activity) => sent ??= activity.Request;
        client.CommandActivity += Watch;
        try
        {
            await query.Commands.CreateEmployee(new() {Name = "Eve", DepartmentId = 2, Status = Status.PartTime});
        }
        finally
        {
            client.CommandActivity -= Watch;
        }

        await Assert.That(sent!.Payload.GetProperty("status").GetString()).IsEqualTo("PartTime");
    }

    [Test]
    public async Task ARefusedPayloadThrows()
    {
        var exception = (await Assert.ThrowsExactlyAsync<ScryRequestException>(() => query.Commands.CreateEmployee(new() {Name = "", DepartmentId = 1})))!;

        await Assert.That(exception.Code).IsEqualTo(ScryErrorCode.Validation);
    }

    // Past the sync window, the receipt streams: pending, and then the outcome, on the same response.
    [Test]
    public async Task ASlowRenameIsFollowedToItsOutcome()
    {
        var id = await Hire("Slowcoach");
        var slow = ScryClient.ForHttp(app.GetTestClient(), "/api/query");
        slow.CommandWait = TimeSpan.FromMilliseconds(100);

        var outcome = await new ScryQuery(slow).Commands.RenameEmployee(new() {Id = id, Name = "Slowly renamed"});
        await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Pending);
        await Assert.That(slow.PendingWork.PendingCount).IsEqualTo(1);

        var final = await outcome.Completion.WaitAsync(patience);

        using (Assert.Multiple())
        {
            await Assert.That(final.Status).IsEqualTo(ScryCommandStatus.Completed);
            await Assert.That(await Name(id)).IsEqualTo("Slowly renamed");
        }
        await slow.DisposeAsync();
    }

    // Only an inactive employee may be deleted — the command's policy, decided per row, in the database,
    // for every query that reads it — and an active one is refused as a row that is not there.
    [Test]
    public async Task TheDeleteCapabilityIsTheRowCondition()
    {
        var rows = await query.Employee
            .Where(_ => _.Name == "Bob" || _.Name == "Carol")
            .OrderBy(_ => _.Name)
            .Select(_ => new {_.Name, _.CanDeleteEmployee, _.CanRenameEmployee})
            .ToListAsync();

        await Assert.That(rows.Select(_ => (_.Name, _.CanDeleteEmployee, _.CanRenameEmployee))).IsEquivalentTo([("Bob", true, true), ("Carol", false, true)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task DeletingAnActiveEmployeeIsAFailedOutcome()
    {
        var id = await Hire("Stays");

        var outcome = await query.Commands.DeleteEmployee(new() {Id = id});

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Failed);
            await Assert.That(outcome.Error).IsEqualTo(ScryCommandNotFoundException.TargetMessage);
        }
    }

    // Deactivating a row turns its delete on as the live query's next answer, and deleting it takes the
    // row away as the one after — with nothing here asking again.
    [Test]
    public async Task ADeleteArrivesAsTheLiveQuerysNextAnswer()
    {
        var id = await Hire("Leaving");
        await using var answers = query.Employee
            .Where(_ => _.Id == id)
            .Select(_ => new {_.Name, _.CanDeleteEmployee})
            .Live()
            .GetAsyncEnumerator();
        await Assert.That(await Next(answers)).IsTrue();
        await Assert.That(answers.Current.Single().CanDeleteEmployee).IsFalse();

        (await query.Commands.SetEmployeeActive(new() {Id = id, Active = false})).EnsureCompleted();
        await Assert.That(await Next(answers)).IsTrue();
        await Assert.That(answers.Current.Single().CanDeleteEmployee).IsTrue();

        (await query.Commands.DeleteEmployee(new() {Id = id})).EnsureCompleted();
        await Assert.That(await Next(answers)).IsTrue();
        await Assert.That(answers.Current).IsEmpty();
    }

    // The sample's one failure a client is shown in the handler's own words.
    [Test]
    public async Task DeletingAManagerFailsWithTheHandlersMessage()
    {
        var manager = await Hire("Manager");
        await using (var context = database.NewDbContext())
        {
            context.Employees.Add(
                new()
                {
                    Name = "Report",
                    DepartmentId = 1,
                    ManagerId = manager,
                    Active = true
                });
            await context.SaveChangesAsync();
        }

        (await query.Commands.SetEmployeeActive(new() {Id = manager, Active = false})).EnsureCompleted();
        var outcome = await query.Commands.DeleteEmployee(new() {Id = manager});

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Failed);
            await Assert.That(outcome.Error).Contains("manages others");
        }
    }

    // The bus message, served in-process here: RepriceOrder is the same class the NServiceBus sample's
    // worker handles.
    [Test]
    public async Task TheAnnotatedMessageIsACommand()
    {
        var before = await Amount(1);

        var outcome = await query.Commands.RepriceOrder(new() {Id = 1});

        using (Assert.Multiple())
        {
            await Assert.That(outcome.Status).IsEqualTo(ScryCommandStatus.Completed);
            await Assert.That(await Amount(1)).IsEqualTo(before + 1);
        }
    }

    [Test]
    public async Task TheCallerIsToldWhatItMaySend()
    {
        await client.Ready;

        using (Assert.Multiple())
        {
            await Assert.That(query.Commands.CanCreateEmployee).IsTrue();
            await Assert.That(query.Commands.CanDeleteEmployee).IsTrue();
            await Assert.That(query.Commands.CanRepriceOrder).IsTrue();
        }
    }

    static TimeSpan patience = TimeSpan.FromSeconds(20);

    static Task<bool> Next<T>(IAsyncEnumerator<T> answers) =>
        answers.MoveNextAsync().AsTask().WaitAsync(patience);

    static async Task<int> Hire(string name)
    {
        var outcome = await query.Commands.CreateEmployee(new() {Name = name, DepartmentId = 1, Status = Status.FullTime});
        return outcome.EnsureCompleted().Value.Id;
    }

    static async Task<string> Name(int id) =>
        (await query.Employee.Where(_ => _.Id == id).Select(_ => new {_.Name}).SingleAsync())!.Name;

    static async Task<decimal> Amount(int id) =>
        (await query.Order.Where(_ => _.Id == id).Select(_ => new {_.Amount}).SingleAsync())!.Amount;
}
