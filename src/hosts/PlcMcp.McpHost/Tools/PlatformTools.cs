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
    [Description("List planned PLC engineering capabilities and their verification status. Production blocks unverified capabilities. The separate validation host can execute explicitly allowlisted operations as unverified.")]
    public Task<CapabilityCatalog> ListCapabilities(CancellationToken cancellationToken)
        => handler.Handle(new(), cancellationToken);

    [McpServerTool(Name = "plc_runtime_get_status", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Report execution mode and scheduler state. Fixture results are simulated; real InoProShop capabilities are not yet verified.")]
    public async Task<RuntimeStatus> GetRuntimeStatus(CancellationToken cancellationToken)
    {
        var slot = await store.GetSlotAsync(cancellationToken);
        return new("1.4", "P5-engineering-delivery", false, false, executor.ExecutionMode,
            executor.ExecutionMode == "fixture" ? "simulated" : "unverified", slot.Blocked,
            slot.Reason ?? (executor.Validation is not null ? "Explicit real validation session. Only allowlisted operations can run; compatibility remains unverified." : executor.Unavailability?.Message) ?? "Test-only execution; no IDE is connected.", executor.Validation);
    }
}

public sealed record RuntimeStatus(string ContractVersion, string Stage, bool IdeConnected,
    bool EngineeringReady, string ExecutionMode, string EvidenceKind, bool SchedulerBlocked, string Reason, ValidationEvidence? Validation = null);
