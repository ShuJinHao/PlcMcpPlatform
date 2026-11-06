using PlcMcp.McpHost;
using PlcMcp.TestHost;

if (args.FirstOrDefault() == "--maintenance") return await MaintenanceCommand.RunAsync(args);
if (args.Length == 3 && args[0] == "--fixture-worker")
    return await FixtureWorker.RunAsync(args[1], args[2]);
await using var app = await HostApplication.BuildAsync(args, paths => new FixtureExecutor(Path.Combine(paths.DataDirectory, "invocations"), maxExchangeBytes: paths.MaxExchangeBytes));
await app.RunAsync();
return 0;
