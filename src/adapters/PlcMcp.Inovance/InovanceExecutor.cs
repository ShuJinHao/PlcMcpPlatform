using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Inovance;

public sealed record InovanceOptions(string ExecutablePath, string Profile, string ScriptPath, string WorkDirectory,
    int MaxExchangeBytes = PlcMcp.Services.Contracts.Exchange.ExchangeOperations.DefaultMaxXmlBytes,
    string EngineAssemblyPath = "", string[]? IdentityFiles = null, string? AcceptancePath = null, string? EvidenceDirectory = null);

public sealed class InovanceExecutor : IEngineeringExecutor
{
    private readonly InovanceOptions options;
    private readonly IInovanceLaunchProfile launch;
    private readonly InovanceValidationProfile? validationProfile;
    public InovanceAcceptance Acceptance { get; }
    public InovanceExecutor(InovanceOptions options, IInovanceLaunchProfile? launchProfile = null, InovanceValidationProfile? validationProfile = null)
    {
        this.options = options; this.validationProfile = validationProfile;
        launch = launchProfile ?? new ScriptEngine35LaunchProfile(options);
        Acceptance = new(options.AcceptancePath, () =>
        {
            if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("acceptance_windows_required");
            return InovanceIdentity.Observe(options, launch.StrategyId);
        });
        Validation = validationProfile?.Evidence(options.ScriptPath);
    }
    public string ExecutionMode => "real";
    public RuntimeReadiness? Readiness => validationProfile is null ? Acceptance.GetReadiness() : new("validation", null, null, [], null);
    public OperationError? Unavailability => !OperatingSystem.IsWindows() || !File.Exists(options.ExecutablePath)
        ? new("ide_unavailable", "A configured Windows InoProShop installation is required for real execution.")
        : Readiness?.State == "accepted" ? null : new(Readiness?.ErrorCode ?? "capability_unverified", "No matching reviewed acceptance is loaded. Local tools remain available.");
    public ValidationEvidence? Validation { get; }
    public OperationError? CheckAvailability(string operation)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(options.ExecutablePath)) return new("ide_unavailable", "A configured Windows InoProShop installation is required.");
        if (validationProfile is null) return Acceptance.CheckOperation(operation);
        if (validationProfile.Profile != options.Profile) return new("ide_profile_mismatch", "Validation and launch profiles must match.");
        return validationProfile.Allows(operation) ? null : new("operation_not_in_validation_scope", "Add this operation to the explicit local validation profile before testing it.");
    }
    public Task<OperationError?> CheckRequestAsync(string operation, EngineeringInput input, CancellationToken token) => Task.FromResult(
        CheckAvailability(operation) ?? (validationProfile is null ? Acceptance.CheckInput(operation, JsonSerializer.SerializeToElement(input, WorkerJson.Options)) : null));

    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken cancellationToken)
    {
        if (CheckAvailability(request.Operation) is { } unavailable) return WorkerJson.Failed(request, unavailable.Code);
        if (validationProfile is null && Acceptance.CheckInput(request.Operation, request.Payload) is { } outside) return WorkerJson.Failed(request, outside.Code);
        if (!File.Exists(options.ScriptPath) || string.IsNullOrWhiteSpace(options.Profile) || request.ExecutionMode != ExecutionMode)
            return WorkerJson.Failed(request, "ide_unavailable");
        if (request.Payload.TryGetProperty("ideProfile", out var requiredProfile) && requiredProfile.ValueKind == JsonValueKind.String
            && requiredProfile.GetString() != options.Profile) return WorkerJson.Failed(request, "ide_profile_mismatch");
        if (validationProfile is not null && validationProfile.Evidence(options.ScriptPath).ScriptSha256 != Validation!.ScriptSha256)
            return WorkerJson.Failed(request, "validation_scripts_changed_restart_required");
        InovanceIdentity? identity = null;
        if (!string.IsNullOrWhiteSpace(options.EngineAssemblyPath))
        {
            try { identity = InovanceIdentity.Observe(options, launch.StrategyId); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            { return WorkerJson.Failed(request, "identity_file_unavailable"); }
        }
        if (validationProfile is null && identity is null) return WorkerJson.Failed(request, "identity_file_unavailable");
        var payload = JsonNode.Parse(request.Payload.GetRawText())!.AsObject();
        if (identity is not null) payload["runtimeIdentityExpected"] = JsonSerializer.SerializeToNode(new
        { executableSha256 = identity.Executable.Sha256, engineSha256 = identity.Engine.Sha256, engineName = identity.Engine.Name }, WorkerJson.Options);
        var invocation = request with { Payload = JsonSerializer.SerializeToElement(payload, WorkerJson.Options) };
        var worker = new ProcessWorker(options.WorkDirectory, launch.CreateInvocation, maxExchangeBytes: options.MaxExchangeBytes);
        var result = await worker.RunAsync(invocation, cancellationToken);
        if (identity is not null && result.Outcome == "succeeded")
        {
            if (result.Payload is not { } returned || !returned.TryGetProperty("runtimeIdentity", out var runtime) || !InovanceAcceptance.RuntimeIdentityMatches(runtime, identity))
                return WorkerJson.Failed(request, "runtime_identity_unproven");
            try
            {
                if (InovanceIdentity.Observe(options, launch.StrategyId).Fingerprint() != identity.Fingerprint()) return WorkerJson.Failed(request, "runtime_identity_changed");
                if (validationProfile is not null && options.EvidenceDirectory is { } directory)
                {
                    Directory.CreateDirectory(directory);
                    var evidence = new InovanceRunEvidence(1, "real", false, DateTimeOffset.UtcNow, identity, runtime, request, result);
                    var target = Path.Combine(directory, request.RequestId.ToString("N") + ".json");
                    var temporary = target + ".tmp";
                    await File.WriteAllBytesAsync(temporary, JsonSerializer.SerializeToUtf8Bytes(evidence, WorkerJson.Options), cancellationToken);
                    File.Move(temporary, target, false);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
            { return WorkerJson.Failed(request, "validation_evidence_unavailable"); }
        }
        if (result.Payload is { ValueKind: JsonValueKind.Object } value)
        {
            var enriched = JsonNode.Parse(value.GetRawText())!.AsObject();
            enriched["validation"] = JsonSerializer.SerializeToNode(Validation, WorkerJson.Options);
            if (validationProfile is null) enriched["acceptance"] = JsonSerializer.SerializeToNode(Readiness, WorkerJson.Options);
            result = result with { Payload = JsonSerializer.SerializeToElement(enriched, WorkerJson.Options) };
        }
        return result;
    }
}
