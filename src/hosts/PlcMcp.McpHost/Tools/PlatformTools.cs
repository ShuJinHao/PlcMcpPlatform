using System.ComponentModel;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Capabilities;
using PlcMcp.Services.Contracts.Capabilities;
using PlcMcp.Services.Contracts.Messaging;
using PlcMcp.Services.Contracts.Execution;

namespace PlcMcp.McpHost.Tools;

[McpServerToolType]
public sealed class PlatformTools(IQueryHandler<ListCapabilitiesQuery, CapabilityCatalog> handler, IEngineeringExecutor executor, IJobStore store)
{
    [McpServerTool(Name = "plc_capabilities_list", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("List planned PLC engineering capabilities and their verification status. Unverified capabilities cannot be executed.")]
    public Task<CapabilityCatalog> ListCapabilities(CancellationToken cancellationToken)
        => handler.Handle(new(), cancellationToken);

    [McpServerTool(Name = "plc_runtime_get_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Report execution mode and scheduler state. Fixture results are simulated; real InoProShop capabilities are not yet verified.")]
    public async Task<RuntimeStatus> GetRuntimeStatus(CancellationToken cancellationToken)
    {
        var slot = await store.GetSlotAsync(cancellationToken);
        return new("1.0", "P2-managed-editing", false, false, executor.ExecutionMode,
            executor.ExecutionMode == "fixture" ? "simulated" : "unverified", slot.Blocked,
            slot.Reason ?? executor.Unavailability?.Message ?? "Test-only execution; no IDE is connected.");
    }
}

public sealed record RuntimeStatus(string ContractVersion, string Stage, bool IdeConnected,
    bool EngineeringReady, string ExecutionMode, string EvidenceKind, bool SchedulerBlocked, string Reason);
