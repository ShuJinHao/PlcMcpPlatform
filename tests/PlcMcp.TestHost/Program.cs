using PlcMcp.McpHost;
using PlcMcp.TestHost;

if (args.Length == 3 && args[0] == "--fixture-worker")
    return await FixtureWorker.RunAsync(args[1], args[2]);
await using var app = await HostApplication.BuildAsync(args, paths => new FixtureExecutor(Path.Combine(paths.DataDirectory, "invocations")));
await app.RunAsync();
return 0;
