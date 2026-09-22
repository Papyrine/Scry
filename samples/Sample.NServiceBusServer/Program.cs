// Run this, then the worker it prints the command for, then watch it:
//   dotnet run --project samples/Sample.ConsoleClient -- --live --server http://localhost:5101
// and send the command the worker handles:
//   dotnet run --project samples/Sample.ConsoleClient -- --reprice 1 --server http://localhost:5101
// The console reprints, though nothing in this process wrote anything.
var app = await NServiceBusServerHost.Build(args.Contains("--urls") ? args : [.. args, "--urls", "http://localhost:5101"]);
await app.RunAsync();
