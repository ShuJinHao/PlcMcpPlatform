using PlcMcp.McpHost;

if (args.FirstOrDefault() == "--maintenance") return await MaintenanceCommand.RunAsync(args);
await using var app = await HostApplication.BuildAsync(args);
await app.RunAsync();
return 0;
