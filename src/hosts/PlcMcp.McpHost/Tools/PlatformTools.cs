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
    [Description("Report local tool availability, reviewed operation scopes, identity failures and scheduler state. No persistent IDE connection or PLC runtime guarantee is implied.")]
    public async Task<RuntimeStatus> GetRuntimeStatus(CancellationToken cancellationToken)
    {
        var slot = await store.GetSlotAsync(cancellationToken);
        var readiness = executor.Readiness;
        var available = readiness?.State == "accepted" && !slot.Blocked && executor.Unavailability is null;
        return new(PlatformRelease.ContractVersion, PlatformRelease.Stage, false, available, executor.ExecutionMode,
            executor.ExecutionMode == "fixture" ? "simulated" : available ? "accepted_scope" : "unverified", slot.Blocked,
            slot.Reason ?? (executor.Validation is not null ? "Explicit real validation session. Only allowlisted operations can run; compatibility remains unverified." : executor.Unavailability?.Message) ?? (available ? "Reviewed operations and conditions only. IDE processes are started per job; PLC runtime behavior remains unverified." : "No accepted IDE operations are available."), executor.Validation, readiness, ToolAccessCatalog.Inspect(executor));
    }
}

public sealed record RuntimeStatus(string ContractVersion, string Stage, bool IdeConnected,
    bool EngineeringReady, string ExecutionMode, string EvidenceKind, bool SchedulerBlocked, string Reason, ValidationEvidence? Validation = null, RuntimeReadiness? Readiness = null, ToolAvailability[]? Tools = null);
