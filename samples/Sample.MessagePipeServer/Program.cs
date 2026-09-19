// A Scry server whose nodes tell each other what they wrote through MessagePipe. Run it twice, watch
// one node with the console client, write to the other — see BackplaneHost for the commands. The
// transport under MessagePipe here is Redis, so it needs one: docker run -p 6379:6379 redis
var builder = WebApplication.CreateBuilder(args);
var database = await BackplaneHost.Database(args, "Sample.MessagePipeServer");
BackplaneHost.AddData(builder.Services, database);

// begin-snippet: sampleMessagePipeBackplane
// MessagePipe and a distributed transport for it, registered as a host that uses MessagePipe for
// anything else already has them. Scry asks for neither by name: it resolves MessagePipe's
// distributed publisher and subscriber, and whichever transport backs them is the one used.
builder.Services
    .AddMessagePipe(_ => _.EnableAutoRegistration = false)
    .AddRedis(ConnectionMultiplexer.Connect(builder.Configuration["Redis"] ?? "localhost:6379"));

builder.Services.AddScry<SampleContext>(
    _ =>
    {
        BackplaneHost.Configure(_);
        _.UseMessagePipeBackplane();
    });
// end-snippet

var app = builder.Build();
BackplaneHost.Map(app);
await app.RunAsync();
