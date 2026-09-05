using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// The endpoint's path asks the database asynchronously for everything a request needs — the rows
/// of a list or a page, a folded terminal, a denied-row probe, a cached policy's refresh, an
/// attachment's bytes — so a request thread is never held on a round trip. Watched through a command
/// interceptor: a blocking ask reaches the interceptor's synchronous member, an awaited one its
/// asynchronous member, and the endpoint's path must reach only the second.
/// </summary>
[TestFixture]
public class AsyncExecutionTests
{
    [Test]
    public async Task AListIsReadAsynchronously() =>
        await Buffered(SharedProcessor.Instance, QueryRequest.Create("Employee", [new TakeOp(2)]));

    [Test]
    public async Task APageIsReadAsynchronously() =>
        await Buffered(SharedProcessor.Instance, QueryRequest.Create("Employee", [new OrderByOp(new MemberNode(["Name"]), Descending: false), new PageOp(Size: 2)]));

    [Test]
    public async Task ACountIsReadAsynchronously() =>
        await Buffered(SharedProcessor.Instance, QueryRequest.Create("Employee", [new CountOp()]));

    [Test]
    public async Task ASingleRowIsReadAsynchronously() =>
        await Buffered(SharedProcessor.Instance, QueryRequest.Create("Employee", [new OrderByOp(new MemberNode(["Name"]), Descending: false), new FirstOp(OrDefault: false)]));

    [Test]
    public async Task AnAggregateIsReadAsynchronously() =>
        await Buffered(SharedProcessor.Instance, QueryRequest.Create("Order", [new AggregateOp(AggregateFn.Sum, new MemberNode(["Amount"]))]));

    // The probe is the one read here: the one inactive employee is denied, so the request fails
    // before its count runs.
    [Test]
    public void ADeniedRowProbeIsReadAsynchronously()
    {
        var spy = new CommandSpy();
        Assert.ThrowsAsync<ScryPermissionException>(
            () => Buffered(Erroring(), QueryRequest.Create("Employee", [new CountOp()]), spy).AsTask());

        Assert.That(spy.Asynchronous, Is.EqualTo(1));
    }

    // A cold scope decides every row, which reads them all before the count is asked.
    [Test]
    public async Task ACachedPolicysRefreshIsReadAsynchronously()
    {
        var policy = new CountingRegionPolicy();
        var processor = ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AddCachedPolicy<Order, long, CountingRegionPolicy>(order => order.Revision);
        });

        var spy = await Buffered(processor, QueryRequest.Create("Order", [new CountOp()]), services: new OnlyPolicy(policy));

        Assert.Multiple(() =>
        {
            Assert.That(policy.Decisions, Is.GreaterThan(0));
            Assert.That(spy.Asynchronous, Is.EqualTo(2));
        });
    }

    [Test]
    public async Task AnAttachmentIsReadAsynchronously()
    {
        var spy = new CommandSpy();
        await using var data = Watched(spy);

        var result = await SharedProcessor.Instance.FetchAttachmentAsync(
            AttachmentRequest.Create("Contract", "Document", [new("1", ClrTypeTag.Int32)]),
            data,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            new HeaderDictionary(),
            Cancel.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Found, Is.True);
            Assert.That(spy.Synchronous, Is.Zero);
            Assert.That(spy.Asynchronous, Is.EqualTo(1));
        });
    }

    // What comes before a stream's first row — here the probe, which passes since the filter
    // excludes the denied row — is awaited as the rows themselves already were.
    [Test]
    public async Task AStreamsProbeIsReadAsynchronously()
    {
        var spy = new CommandSpy();
        await using var data = Watched(spy);
        var active = new WhereOp(new BinaryNode(BinaryOp.Equal, new MemberNode(["Active"]), new ConstNode("true", ClrTypeTag.Boolean)));

        var (_, _, rows) = await Erroring().StreamBufferedAsync(
            QueryRequest.Create("Employee", [active]),
            data,
            EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            new HeaderDictionary());
        var count = 0;
        await foreach (var _ in rows)
        {
            count++;
        }

        Assert.Multiple(() =>
        {
            Assert.That(count, Is.EqualTo(3));
            Assert.That(spy.Synchronous, Is.Zero);
            Assert.That(spy.Asynchronous, Is.EqualTo(2));
        });
    }

    static async ValueTask<CommandSpy> Buffered(ScryProcessor processor, QueryRequest request, CommandSpy? spy = null, IServiceProvider? services = null)
    {
        spy ??= new();
        await using var data = Watched(spy);
        var output = new ArrayBufferWriter<byte>();

        await processor.TryExecuteBufferedAsync(
            request,
            data,
            services ?? EmptyServiceProvider.Instance,
            new HeaderDictionary(),
            new HeaderDictionary(),
            output);

        Assert.Multiple(() =>
        {
            Assert.That(output.WrittenCount, Is.GreaterThan(0));
            Assert.That(spy.Synchronous, Is.Zero);
            Assert.That(spy.Asynchronous, Is.GreaterThan(0));
        });
        return spy;
    }

    static ScryProcessor Erroring() =>
        ScryProcessor.Create<TestContext>(options =>
        {
            options.AddPocoSource<Holiday>(_ => Holiday.Seed());
            options.AddPolicy<Employee, ActiveOnlyPolicy>(new()
            {
                RootList = DeniedRowMode.Error
            });
        });

    // The shared seed, read through a context that reports how each command was run.
    static TestContext Watched(CommandSpy spy)
    {
        using var seeded = TestContext.CreateSeeded();
        return new(new DbContextOptionsBuilder<TestContext>()
            .UseSqlServer(seeded.Database.GetConnectionString())
            .AddInterceptors(spy)
            .Options);
    }

    sealed class OnlyPolicy(CountingRegionPolicy policy) :
        IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(CountingRegionPolicy) ? policy : null;
    }

    sealed class CommandSpy :
        DbCommandInterceptor
    {
        public int Synchronous { get; private set; }

        public int Asynchronous { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Synchronous++;
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, Cancel cancellationToken = default)
        {
            Asynchronous++;
            return new(result);
        }

        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
        {
            Synchronous++;
            return result;
        }

        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, Cancel cancellationToken = default)
        {
            Asynchronous++;
            return new(result);
        }

        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            Synchronous++;
            return result;
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, Cancel cancellationToken = default)
        {
            Asynchronous++;
            return new(result);
        }
    }
}
