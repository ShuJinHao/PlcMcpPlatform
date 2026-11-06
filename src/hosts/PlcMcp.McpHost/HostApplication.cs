using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.EntityFrameworkCore;
using PlcMcp.Execution;
using PlcMcp.Inovance;
using PlcMcp.McpHost.Tools;
using PlcMcp.Services.Contracts.Execution;
using Serilog;

namespace PlcMcp.McpHost;

public sealed record HostPaths(string DataDirectory, string WorkspaceRoot);
public static class HostApplication
{
    // Fixture composition comes from a separate tests/ executable, never production configuration.
    public static async Task<WebApplication> BuildAsync(string[] args, Func<HostPaths, IEngineeringExecutor>? testExecutor = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var fixture = testExecutor is not null;
        if (fixture && !builder.Environment.IsDevelopment()) throw new InvalidOperationException("Test host requires Development environment.");
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5088));
        var key = builder.Configuration["PlcMcp:ApiKey"];
        if (string.IsNullOrWhiteSpace(key) || key.Length < 32)
            throw new InvalidOperationException("Set PlcMcp__ApiKey to a random value of at least 32 characters.");
        var expectedHash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var configuredData = builder.Configuration["PlcMcp:DataDirectory"];
        if (fixture && string.IsNullOrWhiteSpace(configuredData)) throw new InvalidOperationException("Test host requires its own data directory.");
        var data = Path.GetFullPath(configuredData ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PlcMcpPlatform", "real"));
        var paths = new HostPaths(data, Path.GetFullPath(builder.Configuration["PlcMcp:WorkspaceRoot"] ?? Path.Combine(data, "workspaces")));
        Directory.CreateDirectory(paths.WorkspaceRoot);
        builder.Services.AddSerilog(config => config.WriteTo.Console());
        builder.Services.AddPlcMcpApplication();
        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(_ => new InstanceOwnership(paths.DataDirectory));
        IEngineeringExecutor rawWorker = testExecutor?.Invoke(paths) ?? new InovanceExecutor(new(
            builder.Configuration["PlcMcp:Inovance:ExecutablePath"] ?? "", builder.Configuration["PlcMcp:Inovance:Profile"] ?? "",
            Path.Combine(AppContext.BaseDirectory, "Scripts", "engineering_worker.py"), Path.Combine(data, "invocations")));
        builder.Services.AddSingleton<IWorkspaceFiles>(new WorkspaceFiles(paths.WorkspaceRoot));
        var options = new DbContextOptionsBuilder<PlcMcpDbContext>().UseSqlite(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = Path.Combine(data, "execution.db"), DefaultTimeout = 10 }.ToString()).Options;
        builder.Services.AddSingleton<IJobStore>(_ => new SqliteJobStore(options, fixture ? "fixture" : "real"));
        builder.Services.AddSingleton<IManagedProjectFiles>(new ManagedProjectFiles(paths.WorkspaceRoot));
        builder.Services.AddSingleton<IProjectChangeStore>(new SqliteProjectChangeStore(options));
        builder.Services.AddSingleton<IEngineeringExecutor>(sp => new ManagedProjectExecutor(rawWorker, sp.GetRequiredService<IManagedProjectFiles>(), sp.GetRequiredService<IProjectChangeStore>()));
        builder.Services.AddSingleton<ManagedEditingJobs>();
        builder.Services.AddSingleton<EngineeringJobs>();
        builder.Services.AddSingleton<JobDispatcher>();
        builder.Services.AddSingleton<ExecutionSignal>();
        builder.Services.AddHostedService<ExecutionService>();
        var mcp = builder.Services.AddMcpServer()
            .WithHttpTransport(o => o.SessionMode = HttpServerSessionMode.Stateless).WithTools<PlatformTools>();
        if (fixture) mcp.WithTools<EngineeringTools>().WithTools<EditingTools>();
        var app = builder.Build();
        try
        {
            _ = app.Services.GetRequiredService<InstanceOwnership>();
            await app.Services.GetRequiredService<IJobStore>().InitializeAsync(CancellationToken.None);
        }
        catch { await app.DisposeAsync(); throw; }
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/mcp"))
            {
                if (context.Request.Headers.ContainsKey("Origin")) { context.Response.StatusCode = 403; return; }
                var header = context.Request.Headers.Authorization.ToString();
                var supplied = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : "";
                if (!CryptographicOperations.FixedTimeEquals(expectedHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
                { context.Response.StatusCode = 401; return; }
            }
            await next(context);
        });
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", stage = "P2-managed-editing", engineeringReady = false, executionMode = fixture ? "fixture" : "real" }));
        app.MapMcp("/mcp");
        return app;
    }
}
