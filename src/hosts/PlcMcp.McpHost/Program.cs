using PlcMcp.McpHost;

await using var app = await HostApplication.BuildAsync(args);
await app.RunAsync();
