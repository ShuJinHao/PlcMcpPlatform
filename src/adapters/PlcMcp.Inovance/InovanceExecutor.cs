using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Inovance;

public sealed record InovanceOptions(string ExecutablePath, string Profile, string ScriptPath, string WorkDirectory);

public sealed class InovanceExecutor(InovanceOptions options, IInovanceLaunchProfile? launchProfile = null, InovanceValidationProfile? validationProfile = null) : IEngineeringExecutor
{
    public string ExecutionMode => "real";
    public OperationError? Unavailability => !OperatingSystem.IsWindows() || !File.Exists(options.ExecutablePath)
        ? new("ide_unavailable", "A configured Windows InoProShop installation is required for real execution.")
        : new("capability_unverified", "This IDE version has not passed integration validation; production tools remain disabled.");

    public ValidationEvidence? Validation { get; } = validationProfile?.Evidence(options.ScriptPath);
    public OperationError? CheckAvailability(string operation)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(options.ExecutablePath)) return new("ide_unavailable", "A configured Windows InoProShop installation is required.");
        if (validationProfile is null) return Unavailability;
        if (validationProfile.Profile != options.Profile) return new("ide_profile_mismatch", "Validation and launch profiles must match.");
        return validationProfile.Allows(operation) ? null : new("operation_not_in_validation_scope", "Add this operation to the explicit local validation profile before testing it.");
    }

    // Invoked only by an explicit integration harness until per-version evidence is implemented.
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (CheckAvailability(request.Operation) is { } unavailable) return WorkerJson.Failed(request, unavailable.Code);
        if (!OperatingSystem.IsWindows() || !File.Exists(options.ExecutablePath) || !File.Exists(options.ScriptPath)
            || string.IsNullOrWhiteSpace(options.Profile) || request.ExecutionMode != ExecutionMode)
            return WorkerJson.Failed(request, "ide_unavailable");
        if (request.Payload.TryGetProperty("ideProfile", out var requiredProfile) && requiredProfile.ValueKind == System.Text.Json.JsonValueKind.String
            && requiredProfile.GetString() != options.Profile) return WorkerJson.Failed(request, "ide_profile_mismatch");
        var profile = launchProfile ?? new ScriptEngine35LaunchProfile(options);
        var worker = new ProcessWorker(options.WorkDirectory, profile.CreateInvocation);
        if (validationProfile!.Evidence(options.ScriptPath).ScriptSha256 != Validation!.ScriptSha256)
            return WorkerJson.Failed(request, "validation_scripts_changed_restart_required");
        var result = await worker.RunAsync(request, cancellationToken);
        if (result.Payload is { ValueKind: JsonValueKind.Object } payload)
        {
            var enriched = JsonNode.Parse(payload.GetRawText())!.AsObject();
            enriched["validation"] = JsonSerializer.SerializeToNode(Validation, WorkerJson.Options);
            result = result with { Payload = JsonSerializer.SerializeToElement(enriched, WorkerJson.Options) };
        }
        return result;
    }
}
