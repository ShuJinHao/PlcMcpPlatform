using PlcMcp.McpHost;

if (args.FirstOrDefault() == "--maintenance") return await MaintenanceCommand.RunAsync(args);
await using var app = await HostApplication.BuildAsync(args, validation: true);
await app.RunAsync();
return 0;
