using System.Transactions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// What tells a live query its data changed. Every test here is about when a change is reported and
/// what it names — because one reported too early is read before it exists and never looked for again,
/// and one that names the wrong entity reaches nobody.
/// </summary>
[TestFixture]
public class ChangeSignalTests
{
    [Test]
    public async Task ASaveReportsTheRootOfWhatItWrote()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalSave");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        // A derived type's rows are read through its root, so the root is what a query over either
        // has to hear about.
        context.Add(
            new Vehicle
            {
                Name = "Van",
                Wheels = 4
            });
        await context.SaveChangesAsync();

        Assert.That(reported.Single().Entities, Is.EqualTo([Name<Asset>(context)]));
    }

    [Test]
    public async Task ASynchronousSaveReportsToo()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalSaveSync");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        context.Add(
            new Department
            {
                Name = "Legal"
            });

        // ReSharper disable once MethodHasAsyncOverload
        context.SaveChanges();

        Assert.That(reported.Single().Entities, Is.EqualTo([Name<Department>(context)]));
    }

    // Change detection has not run when the interceptor is asked, so a property set on a tracked
    // entity is still Unchanged unless something looks.
    [Test]
    public async Task AModifiedPropertyIsSeenWithoutAnExplicitDetect()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalModify");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);
        var department = new Department
        {
            Name = "Legal"
        };
        context.Add(department);
        await context.SaveChangesAsync();
        reported.Clear();

        department.Name = "Counsel";
        await context.SaveChangesAsync();

        Assert.That(reported.Single().Entities, Is.EqualTo([Name<Department>(context)]));
    }

    [Test]
    public async Task ASaveThatWroteNothingReportsNothing()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalEmpty");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        await context.SaveChangesAsync();

        Assert.That(reported, Is.Empty);
    }

    // Reported before the commit, a live query would read the rows as they were, find its answer
    // unchanged, and have no reason to look again.
    [Test]
    public async Task ASaveInsideATransactionReportsOnCommitAndNotBefore()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalCommit");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        await using var transaction = await context.Database.BeginTransactionAsync();
        context.Add(
            new Department
            {
                Name = "Legal"
            });
        await context.SaveChangesAsync();
        context.Add(
            new Vehicle
            {
                Name = "Van"
            });
        await context.SaveChangesAsync();

        Assert.That(reported, Is.Empty);

        await transaction.CommitAsync();

        // Both saves, once, as one change.
        Assert.That(
            reported.Single().Entities,
            Is.EquivalentTo([Name<Department>(context), Name<Asset>(context)]));
    }

    [Test]
    public async Task ARolledBackTransactionReportsNothing()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalRollback");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            context.Add(
                new Department
                {
                    Name = "Legal"
                });
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        // Nor is it carried into whatever the context commits next.
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            await transaction.CommitAsync();
        }

        Assert.That(reported, Is.Empty);
    }

    [Test]
    public async Task AnAmbientTransactionReportsWhenItCompletes()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalAmbient");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            context.Add(
                new Department
                {
                    Name = "Legal"
                });
            await context.SaveChangesAsync();

            Assert.That(reported, Is.Empty);

            scope.Complete();
        }

        Assert.That(reported.Single().Entities, Is.EqualTo([Name<Department>(context)]));
    }

    [Test]
    public async Task AnAbandonedAmbientTransactionReportsNothing()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalAmbientAbandon");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        using (new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            context.Add(
                new Department
                {
                    Name = "Legal"
                });
            await context.SaveChangesAsync();
        }

        Assert.That(reported, Is.Empty);
    }

    [Test]
    public async Task AFailedSaveReportsNothing()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalFailure");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        // No such order, so the foreign key refuses it.
        context.Add(
            new OrderLine
            {
                OrderId = 404
            });

        Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.That(reported, Is.Empty);
    }

    // The lines are never loaded, so the tracker holds no entry for them: the database deletes them
    // on its own, and nothing but the model says so.
    [Test]
    public async Task ADeleteReportsWhatTheDatabaseCascadesTo()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalCascade");
        int id;
        await using (var seeding = database.NewDbContext())
        {
            var order = new Order
            {
                Region = "North"
            };
            seeding.Add(order);
            seeding.Add(
                new OrderLine
                {
                    Order = order
                });
            await seeding.SaveChangesAsync();
            id = order.Id;
        }

        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);
        context.Remove(
            new Order
            {
                Id = id
            });
        await context.SaveChangesAsync();

        Assert.That(
            reported.Single().Entities,
            Is.EquivalentTo([Name<Order>(context), Name<OrderLine>(context)]));
    }

    [Test]
    public void NotifyReportsADerivedTypeAsItsRoot()
    {
        using var context = TestContext.CreateSeeded();
        var (changes, reported) = Listening();
        changes.Attach(context.Model);

        changes.Notify<HeavyPress>();

        Assert.That(reported.Single().Entities, Is.EqualTo([Name<Machine>(context)]));
    }

    [Test]
    public void NotifyNamesEachRootOnce()
    {
        using var context = TestContext.CreateSeeded();
        var (changes, reported) = Listening();
        changes.Attach(context.Model);

        changes.Notify(typeof(Vehicle), typeof(Building), typeof(Order));

        Assert.That(
            reported.Single().Entities,
            Is.EqualTo([Name<Asset>(context), Name<Order>(context)]));
    }

    // A type the model does not map is a POCO source, which only its host can report on.
    [Test]
    public void NotifyNamesAnUnmappedTypeAsItself()
    {
        using var context = TestContext.CreateSeeded();
        var (changes, reported) = Listening();
        changes.Attach(context.Model);

        changes.Notify<Holiday>();

        Assert.That(reported.Single().Entities, Is.EqualTo([typeof(Holiday).FullName]));
    }

    // Which root a type's rows are read through is the model's to say. With none seen yet, a report
    // that might name the wrong thing is widened rather than risked.
    [Test]
    public void NotifyBeforeAModelIsSeenReportsEverything()
    {
        var (changes, reported) = Listening();

        changes.Notify<Vehicle>();

        Assert.That(reported.Single().Everything, Is.True);
    }

    [Test]
    public void NotifyWithNothingToNameReportsNothing()
    {
        var (changes, reported) = Listening();

        changes.Notify();

        Assert.That(reported, Is.Empty);
    }

    [Test]
    public void NotifyAllReportsEverything()
    {
        var (changes, reported) = Listening();

        changes.NotifyAll();

        Assert.Multiple(() =>
        {
            Assert.That(reported.Single().Everything, Is.True);
            Assert.That(reported.Single().Origin, Is.EqualTo(changes.Origin));
        });
    }

    [Test]
    public void AListenerThatLeftHearsNothingMore()
    {
        var changes = new ScryChanges();
        List<ScryChange> reported = [];
        var listening = changes.Listen(reported.Add);

        listening.Dispose();
        changes.NotifyAll();

        Assert.That(reported, Is.Empty);
    }

    // No row was written, but which rows a caller may see is part of what a live query answers.
    [Test]
    public void APolicyCacheInvalidationReportsTheEntity()
    {
        using var context = TestContext.CreateSeeded();
        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision);
        });
        processor.Changes.Attach(context.Model);
        List<ScryChange> reported = [];
        using var listening = processor.Changes.Listen(reported.Add);

        processor.PolicyCache.InvalidateScope<Order>("anyone");
        processor.PolicyCache.InvalidateRows<Order>([1]);

        Assert.That(
            reported.Select(_ => _.Entities.Single()),
            Is.EqualTo([Name<Order>(context), Name<Order>(context)]));
    }

    [Test]
    public async Task AChangeReachesTheOtherNodeAndDoesNotEcho()
    {
        var backplane = new LoopbackBackplane();
        var (here, heardHere) = Listening(backplane);
        var (there, heardThere) = Listening(backplane);
        await here.Reconciled;
        await there.Reconciled;

        here.Raise(["Order"]);

        Assert.Multiple(() =>
        {
            // Once here, from the raise itself: the backplane handing it back is recognised.
            Assert.That(heardHere.Single().Entities, Is.EqualTo(["Order"]));
            Assert.That(heardThere.Single().Entities, Is.EqualTo(["Order"]));
            Assert.That(heardThere.Single().Origin, Is.EqualTo(here.Origin));
            Assert.That(backplane.Published, Has.Count.EqualTo(1));
        });
    }

    // A change heard from another node is acted on, never passed on: every node publishing what it
    // hears would be a storm.
    [Test]
    public async Task AChangeFromAnotherNodeIsNotPublishedAgain()
    {
        var backplane = new LoopbackBackplane();
        var (here, _) = Listening(backplane);
        var (there, _) = Listening(backplane);
        await here.Reconciled;
        await there.Reconciled;

        here.NotifyAll();

        Assert.That(backplane.Published.Select(_ => _.Origin), Is.EqualTo([here.Origin]));
    }

    // A node with no live query of its own still has to say what it wrote, and has no reason to listen.
    [Test]
    public async Task ANodeNobodyListensOnPublishesWithoutSubscribing()
    {
        var backplane = new LoopbackBackplane();
        var changes = new ScryChanges().Attach(Services(backplane));
        await changes.Reconciled;

        changes.NotifyAll();

        Assert.Multiple(() =>
        {
            Assert.That(backplane.Subscriptions, Is.Zero);
            Assert.That(backplane.Published, Has.Count.EqualTo(1));
        });
    }

    [Test]
    public async Task TheBackplaneSubscriptionLastsAsLongAsSomethingListens()
    {
        var backplane = new LoopbackBackplane();
        var changes = new ScryChanges().Attach(Services(backplane));

        var first = changes.Listen(_ => { });
        var second = changes.Listen(_ => { });
        await changes.Reconciled;
        Assert.That(backplane.Subscriptions, Is.EqualTo(1));

        first.Dispose();
        await changes.Reconciled;
        Assert.That(backplane.Subscriptions, Is.EqualTo(1));

        second.Dispose();
        await changes.Reconciled;
        Assert.That(backplane.Subscriptions, Is.Zero);
    }

    // Raised inside the host's SaveChanges, where a backplane that is down must cost the write nothing.
    [Test]
    public void ABackplaneThatThrowsCostsTheWriterNothing()
    {
        var backplane = new LoopbackBackplane
        {
            Failing = true
        };
        var (changes, reported) = Listening(backplane);

        Assert.DoesNotThrow(changes.NotifyAll);

        Assert.Multiple(() =>
        {
            // This node's own live queries still hear of this node's own write.
            Assert.That(reported, Has.Count.EqualTo(1));
            Assert.That(changes.BackplaneFailures, Is.EqualTo(1));
        });
    }

    // An entity name is EF's, and a shared-type entity's carries commas and brackets.
    [Test]
    public void AChangeSurvivesBeingCarriedAsText()
    {
        var change = new ScryChange(["Sample.Order", "ArticleLabel (Dictionary<string, object>)"], Guid.NewGuid());

        Assert.That(ScryChange.TryParse(change.Serialize(), out var parsed), Is.True);

        Assert.Multiple(() =>
        {
            Assert.That(parsed!.Entities, Is.EqualTo(change.Entities));
            Assert.That(parsed.Origin, Is.EqualTo(change.Origin));
        });
    }

    [Test]
    public void EverythingSurvivesBeingCarriedAsText()
    {
        var change = new ScryChange([], Guid.NewGuid());

        Assert.That(ScryChange.TryParse(change.Serialize(), out var parsed), Is.True);
        Assert.That(parsed!.Everything, Is.True);
    }

    // A backplane is shared infrastructure: what is not one of these is somebody else's message.
    [TestCase("")]
    [TestCase("not json")]
    [TestCase("[]")]
    [TestCase("{}")]
    [TestCase("""{"origin":"nobody","entities":[]}""")]
    [TestCase("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11"}""")]
    [TestCase("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11","entities":"Order"}""")]
    [TestCase("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11","entities":[null]}""")]
    [TestCase("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11","entities":[7]}""")]
    public void WhatIsNotAChangeIsNotReadAsOne(string text) =>
        Assert.That(ScryChange.TryParse(text, out _), Is.False);

    // An owned type's rows are only ever read through their owner, and a join table has no CLR type
    // of its own to be told apart by — which is why a name is what travels.
    [Test]
    public void OwnedTypesAndJoinTablesAreNamedByWhatTheyAreReadThrough()
    {
        using var context = new ShapesContext(
            new DbContextOptionsBuilder<ShapesContext>()
                .UseSqlServer("Server=(none)")
                .Options);
        var model = context.Model;
        var article = model.FindEntityType(typeof(Article))!;
        var byline = model.FindEntityTypes(typeof(Byline)).Single();
        var join = article.GetSkipNavigations().Single().JoinEntityType;

        Assert.Multiple(() =>
        {
            Assert.That(EntityNames.Root(byline), Is.EqualTo(article.Name));
            Assert.That(EntityNames.Root(join), Is.EqualTo(join.Name));
            Assert.That(EntityNames.For(model, typeof(Byline)), Is.EqualTo([article.Name]));
        });
    }

    static (ScryChanges Changes, List<ScryChange> Reported) Listening(IScryChangeBackplane? backplane = null)
    {
        var changes = new ScryChanges();
        if (backplane is not null)
        {
            changes.Attach(Services(backplane));
        }

        List<ScryChange> reported = [];
        changes.Listen(reported.Add);
        return (changes, reported);
    }

    static ServiceProvider Services(IScryChangeBackplane backplane) =>
        new ServiceCollection()
            .AddSingleton(backplane)
            .BuildServiceProvider();

    static TestContext Writing(SqlDatabase<TestContext> database, ScryChanges changes) =>
        new(new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer(database.ConnectionString)
            .AddInterceptors(new ScryChangeInterceptor(changes))
            .Options);

    static string Name<TEntity>(DbContext context) =>
        context.Model.FindEntityType(typeof(TEntity))!.Name;

    /// <summary>Delivers to every subscriber, the publisher included, as most real transports do.</summary>
    sealed class LoopbackBackplane :
        IScryChangeBackplane
    {
        List<Func<ScryChange, Cancel, ValueTask>> handlers = [];

        public List<ScryChange> Published { get; } = [];

        public bool Failing { get; init; }

        public int Subscriptions
        {
            get
            {
                lock (handlers)
                {
                    return handlers.Count;
                }
            }
        }

        public async ValueTask PublishAsync(ScryChange change, Cancel cancel)
        {
            if (Failing)
            {
                throw new("The backplane is down.");
            }

            Func<ScryChange, Cancel, ValueTask>[] current;
            lock (handlers)
            {
                Published.Add(change);
                current = [.. handlers];
            }

            foreach (var handler in current)
            {
                await handler(change, cancel);
            }
        }

        public ValueTask<IAsyncDisposable> SubscribeAsync(Func<ScryChange, Cancel, ValueTask> handler, Cancel cancel)
        {
            lock (handlers)
            {
                handlers.Add(handler);
            }

            return new(new Subscription(this, handler));
        }

        sealed class Subscription(LoopbackBackplane owner, Func<ScryChange, Cancel, ValueTask> handler) :
            IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                lock (owner.handlers)
                {
                    owner.handlers.Remove(handler);
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    sealed class ShapesContext(DbContextOptions<ShapesContext> options) :
        DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Article>().OwnsOne(_ => _.Byline);
            builder.Entity<Label>();
        }
    }

    sealed class Article
    {
        public int Id { get; set; }
        public Byline Byline { get; set; } = new();
        public List<Label> Labels { get; set; } = [];
    }

    sealed class Byline
    {
        public string Author { get; set; } = "";
    }

    sealed class Label
    {
        public int Id { get; set; }
        public List<Article> Articles { get; set; } = [];
    }
}
