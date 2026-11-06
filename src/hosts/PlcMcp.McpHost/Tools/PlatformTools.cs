using System.ComponentModel;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Capabilities;
using PlcMcp.Services.Contracts.Capabilities;
using PlcMcp.Services.Contracts.Messaging;

namespace PlcMcp.McpHost.Tools;

[McpServerToolType]
public sealed class PlatformTools(IQueryHandler<ListCapabilitiesQuery, CapabilityCatalog> handler)
{
    [McpServerTool(Name = "plc_capabilities_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List planned PLC engineering capabilities and their verification status. Unverified capabilities cannot be executed.")]
    public Task<CapabilityCatalog> ListCapabilities(CancellationToken cancellationToken)
        => handler.Handle(new(), cancellationToken);

    [McpServerTool(Name = "plc_runtime_get_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Report platform readiness. P0 supports discovery only; no IDE connection, program generation, compilation or PLC access is available yet.")]
    public RuntimeStatus GetRuntimeStatus() => new("1.0", "P0-foundation", false, false,
        "IDE execution and durable scheduling are not implemented.");
}

public sealed record RuntimeStatus(string ContractVersion, string Stage, bool IdeConnected,
    bool EngineeringReady, string Reason);
