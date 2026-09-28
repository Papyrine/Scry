using System.Transactions;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// What tells a live query its data changed. Every test here is about when a change is reported and
/// what it names — because one reported too early is read before it exists and never looked for again,
/// and one that names the wrong entity reaches nobody.
/// </summary>
[NotInParallel]
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

        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Asset>(context)], CollectionOrdering.Matching);
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

        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Department>(context)], CollectionOrdering.Matching);
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

        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Department>(context)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ASaveThatWroteNothingReportsNothing()
    {
        await using var database = await TestContext.CreateIsolated("ChangeSignalEmpty");
        var (changes, reported) = Listening();
        await using var context = Writing(database, changes);

        await context.SaveChangesAsync();

        await Assert.That(reported).IsEmpty();
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

        await Assert.That(reported).IsEmpty();

        await transaction.CommitAsync();

        // Both saves, once, as one change.
        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Department>(context), Name<Asset>(context)]);
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

        await Assert.That(reported).IsEmpty();
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

            await Assert.That(reported).IsEmpty();

            scope.Complete();
        }

        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Department>(context)], CollectionOrdering.Matching);
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

        await Assert.That(reported).IsEmpty();
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

        await Assert.ThrowsExactlyAsync<DbUpdateException>(() => context.SaveChangesAsync());
        await Assert.That(reported).IsEmpty();
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

        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Order>(context), Name<OrderLine>(context)]);
    }

    [Test]
    public async Task NotifyReportsADerivedTypeAsItsRoot()
    {
        using var context = TestContext.CreateSeeded();
        var (changes, reported) = Listening();
        changes.Attach(context.Model);

        changes.Notify<HeavyPress>();

        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Machine>(context)], CollectionOrdering.Matching);
    }

    [Test]
    public async Task NotifyNamesEachRootOnce()
    {
        using var context = TestContext.CreateSeeded();
        var (changes, reported) = Listening();
        changes.Attach(context.Model);

        changes.Notify(typeof(Vehicle), typeof(Building), typeof(Order));

        await Assert.That(reported.Single().Entities).IsEquivalentTo([Name<Asset>(context), Name<Order>(context)], CollectionOrdering.Matching);
    }

    // A type the model does not map is a POCO source, which only its host can report on.
    [Test]
    public async Task NotifyNamesAnUnmappedTypeAsItself()
    {
        using var context = TestContext.CreateSeeded();
        var (changes, reported) = Listening();
        changes.Attach(context.Model);

        changes.Notify<Holiday>();

        await Assert.That(reported.Single().Entities).IsEquivalentTo([typeof(Holiday).FullName], CollectionOrdering.Matching);
    }

    // Which root a type's rows are read through is the model's to say. With none seen yet, a report
    // that might name the wrong thing is widened rather than risked.
    [Test]
    public async Task NotifyBeforeAModelIsSeenReportsEverything()
    {
        var (changes, reported) = Listening();

        changes.Notify<Vehicle>();

        await Assert.That(reported.Single().Everything).IsTrue();
    }

    [Test]
    public async Task NotifyWithNothingToNameReportsNothing()
    {
        var (changes, reported) = Listening();

        changes.Notify();

        await Assert.That(reported).IsEmpty();
    }

    [Test]
    public async Task NotifyAllReportsEverything()
    {
        var (changes, reported) = Listening();

        changes.NotifyAll();

        using (Assert.Multiple())
        {
            await Assert.That(reported.Single().Everything).IsTrue();
            await Assert.That(reported.Single().Origin).IsEqualTo(changes.Origin);
        }
    }

    [Test]
    public async Task AListenerThatLeftHearsNothingMore()
    {
        var changes = new ScryChanges();
        List<ScryChange> reported = [];
        var listening = changes.Listen(reported.Add);

        listening.Dispose();
        changes.NotifyAll();

        await Assert.That(reported).IsEmpty();
    }

    // No row was written, but which rows a caller may see is part of what a live query answers.
    [Test]
    public async Task APolicyCacheInvalidationReportsTheEntity()
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

        await Assert.That(reported.Select(_ => _.Entities.Single())).IsEquivalentTo([Name<Order>(context), Name<Order>(context)], CollectionOrdering.Matching);
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

        using (Assert.Multiple())
        {
            // Once here, from the raise itself: the backplane handing it back is recognised.
            await Assert.That(heardHere.Single().Entities).IsEquivalentTo(["Order"], CollectionOrdering.Matching);
            await Assert.That(heardThere.Single().Entities).IsEquivalentTo(["Order"], CollectionOrdering.Matching);
            await Assert.That(heardThere.Single().Origin).IsEqualTo(here.Origin);
            await Assert.That(backplane.Published).Count().IsEqualTo(1);
        }
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

        await Assert.That(backplane.Published.Select(_ => _.Origin)).IsEquivalentTo([here.Origin], CollectionOrdering.Matching);
    }

    // A node with no live query of its own still has to say what it wrote, and has no reason to listen.
    [Test]
    public async Task ANodeNobodyListensOnPublishesWithoutSubscribing()
    {
        var backplane = new LoopbackBackplane();
        var changes = new ScryChanges().Attach(Services(backplane));
        await changes.Reconciled;

        changes.NotifyAll();

        using (Assert.Multiple())
        {
            await Assert.That(backplane.Subscriptions).IsZero();
            await Assert.That(backplane.Published).Count().IsEqualTo(1);
        }
    }

    [Test]
    public async Task TheBackplaneSubscriptionLastsAsLongAsSomethingListens()
    {
        var backplane = new LoopbackBackplane();
        var changes = new ScryChanges().Attach(Services(backplane));

        var first = changes.Listen(_ => { });
        var second = changes.Listen(_ => { });
        await changes.Reconciled;
        await Assert.That(backplane.Subscriptions).IsEqualTo(1);

        first.Dispose();
        await changes.Reconciled;
        await Assert.That(backplane.Subscriptions).IsEqualTo(1);

        second.Dispose();
        await changes.Reconciled;
        await Assert.That(backplane.Subscriptions).IsZero();
    }

    // Raised inside the host's SaveChanges, where a backplane that is down must cost the write nothing.
    [Test]
    public async Task ABackplaneThatThrowsCostsTheWriterNothing()
    {
        var backplane = new LoopbackBackplane
        {
            Failing = true
        };
        var (changes, reported) = Listening(backplane);

        await Assert.That(changes.NotifyAll).ThrowsNothing();

        using (Assert.Multiple())
        {
            // This node's own live queries still hear of this node's own write.
            await Assert.That(reported).Count().IsEqualTo(1);
            await Assert.That(changes.BackplaneFailures).IsEqualTo(1);
        }
    }

    // An entity name is EF's, and a shared-type entity's carries commas and brackets.
    [Test]
    public async Task AChangeSurvivesBeingCarriedAsText()
    {
        var change = new ScryChange(["Sample.Order", "ArticleLabel (Dictionary<string, object>)"], Guid.NewGuid());

        await Assert.That(ScryChange.TryParse(change.Serialize(), out var parsed)).IsTrue();

        using (Assert.Multiple())
        {
            await Assert.That(parsed!.Entities).IsEquivalentTo(change.Entities, CollectionOrdering.Matching);
            await Assert.That(parsed.Origin).IsEqualTo(change.Origin);
        }
    }

    [Test]
    public async Task EverythingSurvivesBeingCarriedAsText()
    {
        var change = new ScryChange([], Guid.NewGuid());

        await Assert.That(ScryChange.TryParse(change.Serialize(), out var parsed)).IsTrue();
        await Assert.That(parsed!.Everything).IsTrue();
    }

    // A backplane is shared infrastructure: what is not one of these is somebody else's message.
    [Test]
    [Arguments("")]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("{}")]
    [Arguments("""{"origin":"nobody","entities":[]}""")]
    [Arguments("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11"}""")]
    [Arguments("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11","entities":"Order"}""")]
    [Arguments("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11","entities":[null]}""")]
    [Arguments("""{"origin":"8f0f7d0e-5c0a-4a53-9a39-4d4f4f0b2f11","entities":[7]}""")]
    public async Task WhatIsNotAChangeIsNotReadAsOne(string text) =>
        await Assert.That(ScryChange.TryParse(text, out _)).IsFalse();

    // An owned type's rows are only ever read through their owner, and a join table has no CLR type
    // of its own to be told apart by — which is why a name is what travels.
    [Test]
    public async Task OwnedTypesAndJoinTablesAreNamedByWhatTheyAreReadThrough()
    {
        using var context = new ShapesContext(
            new DbContextOptionsBuilder<ShapesContext>()
                .UseSqlServer("Server=(none)")
                .Options);
        var model = context.Model;
        var article = model.FindEntityType(typeof(Article))!;
        var byline = model.FindEntityTypes(typeof(Byline)).Single();
        var join = article.GetSkipNavigations().Single().JoinEntityType;

        using (Assert.Multiple())
        {
            await Assert.That(EntityNames.Root(byline)).IsEqualTo(article.Name);
            await Assert.That(EntityNames.Root(join)).IsEqualTo(join.Name);
            await Assert.That(EntityNames.For(model, typeof(Byline))).IsEquivalentTo([article.Name], CollectionOrdering.Matching);
        }
    }

    // A row the database nulled changed, so its type is named. It was not deleted, so what hangs off it
    // is untouched — and a key the database refuses to act on changes nothing at all.
    [Test]
    public async Task ADeleteNamesWhatTheDatabaseNullsAndStopsThere()
    {
        using var context = new ShelvesContext(
            new DbContextOptionsBuilder<ShelvesContext>()
                .UseSqlServer("Server=(none)")
                .Options);
        var model = context.Model;
        HashSet<string> names = [];

        EntityNames.AddCascades(model.FindEntityType(typeof(Shelf))!, names);

        await Assert.That(names).IsEquivalentTo([model.FindEntityType(typeof(Book))!.Name]);
    }

    [Test]
    public async Task ABackplaneFailureIsCounted()
    {
        List<(string Instrument, long Value, Dictionary<string, object?> Tags)> measurements = [];
        using var listener = Counters(measurements);
        var (changes, _) = Listening(
            new LoopbackBackplane
            {
                Failing = true
            });

        changes.NotifyAll();

        var failure = measurements.Single(_ => _.Instrument == "scry.server.subscription.signal.failures");
        using (Assert.Multiple())
        {
            await Assert.That(failure.Value).IsEqualTo(1);
            await Assert.That(failure.Tags["scry.signal"]).IsEqualTo("backplane");
            await Assert.That(failure.Tags["error.type"]).IsEqualTo(typeof(Exception).FullName);
        }
    }

    // The three ways a host names its backplane, each ending as the one registration AddScry makes.
    [Test]
    public async Task ABackplaneNamedByTypeIsBuiltFromTheContainer()
    {
        using var provider = new ServiceCollection()
            .AddSingleton(new BackplaneSetting("from the container"))
            .AddScry<TestContext>(options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.UseBackplane<ConfiguredBackplane>();
            })
            .BuildServiceProvider();

        var backplane = provider.GetRequiredService<IScryChangeBackplane>();

        using (Assert.Multiple())
        {
            await Assert.That(backplane).IsAssignableTo<ConfiguredBackplane>();
            await Assert.That(((ConfiguredBackplane) backplane).Setting.Value).IsEqualTo("from the container");
            await Assert.That(provider.GetRequiredService<IScryChangeBackplane>()).IsSameReferenceAs(backplane);
        }
    }

    [Test]
    public async Task ABackplaneBuiltByAFactoryIsTheOneRegistered()
    {
        var built = new LoopbackBackplane();
        using var provider = new ServiceCollection()
            .AddScry<TestContext>(options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.UseBackplane(_ => built);
            })
            .BuildServiceProvider();

        await Assert.That(provider.GetRequiredService<IScryChangeBackplane>()).IsSameReferenceAs(built);
    }

    [Test]
    public async Task ABackplanesOwnServicesAreRegisteredBesideIt()
    {
        using var provider = new ServiceCollection()
            .AddScry<TestContext>(options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.UseBackplane(
                    _ => new ConfiguredBackplane(_.GetRequiredService<BackplaneSetting>()),
                    _ => _.AddSingleton(new BackplaneSetting("shared")));
            })
            .BuildServiceProvider();

        var backplane = (ConfiguredBackplane) provider.GetRequiredService<IScryChangeBackplane>();

        await Assert.That(backplane.Setting).IsSameReferenceAs(provider.GetRequiredService<BackplaneSetting>());
    }

    // What the registration is for: a change reported through the container's ScryChanges is published.
    [Test]
    public async Task TheRegisteredBackplaneIsTheOneChangesArePublishedOn()
    {
        var built = new LoopbackBackplane();
        using var provider = new ServiceCollection()
            .AddScry<TestContext>(options =>
            {
                options.AddPocoSource<Holiday>(_ => Holiday.Seed());
                options.UseBackplane(_ => built);
            })
            .BuildServiceProvider();

        provider.GetRequiredService<ScryChanges>().NotifyAll();

        await Assert.That(built.Published.Single().Everything).IsTrue();
    }

    static MeterListener Counters(List<(string Instrument, long Value, Dictionary<string, object?> Tags)> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == ScryInstrumentation.MeterName)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            var read = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                read[tag.Key] = tag.Value;
            }

            lock (measurements)
            {
                measurements.Add((instrument.Name, value, read));
            }
        });
        listener.Start();
        return listener;
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

    sealed record BackplaneSetting(string Value);

    sealed class ConfiguredBackplane(BackplaneSetting setting) :
        IScryChangeBackplane
    {
        public BackplaneSetting Setting => setting;

        public ValueTask PublishAsync(ScryChange change, Cancel cancel) =>
            ValueTask.CompletedTask;

        public ValueTask<IAsyncDisposable> SubscribeAsync(Func<ScryChange, Cancel, ValueTask> handler, Cancel cancel) =>
            throw new NotSupportedException();
    }

    // A shelf's books are nulled off it, a book's pages go with the book, and a loan keeps its shelf
    // from being deleted at all.
    sealed class ShelvesContext(DbContextOptions<ShelvesContext> options) :
        DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            builder.Entity<Book>()
                .HasOne(_ => _.Shelf)
                .WithMany()
                .OnDelete(DeleteBehavior.SetNull);
            builder.Entity<Page>()
                .HasOne(_ => _.Book)
                .WithMany()
                .OnDelete(DeleteBehavior.Cascade);
            builder.Entity<Loan>()
                .HasOne(_ => _.Shelf)
                .WithMany()
                .OnDelete(DeleteBehavior.Restrict);
        }
    }

    sealed class Shelf
    {
        public int Id { get; set; }
    }

    sealed class Book
    {
        public int Id { get; set; }
        public Shelf? Shelf { get; set; }
    }

    sealed class Page
    {
        public int Id { get; set; }
        public Book Book { get; set; } = null!;
    }

    sealed class Loan
    {
        public int Id { get; set; }
        public Shelf Shelf { get; set; } = null!;
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
