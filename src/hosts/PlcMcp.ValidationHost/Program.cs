using PlcMcp.McpHost;

var app = await HostApplication.BuildAsync(args, validation: true);
await app.RunAsync();
