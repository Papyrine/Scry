// A Scry server whose nodes tell each other what they wrote over Redis pub/sub. Run it twice, watch
// one node with the console client, write to the other — see BackplaneHost for the commands. Needs a
// Redis to talk to: docker run -p 6379:6379 redis
var builder = WebApplication.CreateBuilder(args);
var database = await BackplaneHost.Database(args, "Sample.RedisServer");
BackplaneHost.AddData(builder.Services, database);

// begin-snippet: sampleRedisBackplane
// The connection is the host's own, registered the way it would be for anything else that uses Redis.
builder.Services.AddSingleton<IConnectionMultiplexer>(
    _ => ConnectionMultiplexer.Connect(builder.Configuration["Redis"] ?? "localhost:6379"));

builder.Services.AddScry<SampleContext>(
    _ =>
    {
        BackplaneHost.Configure(_);

        // What this node saves is published, and what the others publish re-asks the live queries
        // held here. Nothing else changes: the interceptor still reports, the queries still run
        // through their policies, and a node with no live queries of its own still says what it wrote.
        _.UseRedisBackplane();
    });
// end-snippet

var app = builder.Build();
BackplaneHost.Map(app);
await app.RunAsync();
