using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Server;
using PlcMcp.McpHost;
using PlcMcp.McpHost.Tools;
using Serilog;

var builder = WebApplication.CreateBuilder(args);
// P0 固定回环监听，不能通过环境变量误暴露到局域网。
builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 5088));
var apiKey = builder.Configuration["PlcMcp:ApiKey"];
if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length < 32)
    throw new InvalidOperationException("Set PlcMcp__ApiKey to a random value of at least 32 characters.");
var expectedKeyHash = SHA256.HashData(Encoding.UTF8.GetBytes(apiKey));
builder.Services.AddSerilog(config => config.WriteTo.Console());
builder.Services.ConfigureHttpJsonOptions(options => options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddPlcMcpApplication();
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
    .WithTools<PlatformTools>();
var app = builder.Build();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp"))
    {
        if (context.Request.Headers.ContainsKey("Origin"))
        {
            context.Response.StatusCode = 403;
            return;
        }
        var header = context.Request.Headers.Authorization.ToString();
        var supplied = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header[7..] : string.Empty;
        if (!CryptographicOperations.FixedTimeEquals(expectedKeyHash, SHA256.HashData(Encoding.UTF8.GetBytes(supplied))))
        {
            context.Response.StatusCode = 401;
            return;
        }
    }
    await next(context);
});
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", stage = "P0-foundation", engineeringReady = false }));
app.MapMcp("/mcp");
await app.RunAsync();
