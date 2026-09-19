// Run this, then the worker it prints the command for, then watch it:
//   dotnet run --project samples/Sample.ConsoleClient -- --live --server http://localhost:5101
// and have the worker write:
//   curl -X POST http://localhost:5101/api/orders/1/reprice-via-worker
// The console reprints, though nothing in this process wrote anything.
var app = await NServiceBusServerHost.Build(args.Contains("--urls") ? args : [.. args, "--urls", "http://localhost:5101"]);
await app.RunAsync();
