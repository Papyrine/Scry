using Microsoft.Extensions.Hosting;

// Started with the connection string Sample.NServiceBusServer prints:
//   dotnet run --project samples/Sample.NServiceBusWorker -- --database "<connection string>"
await NServiceBusWorkerHost.Build(args).RunAsync();
