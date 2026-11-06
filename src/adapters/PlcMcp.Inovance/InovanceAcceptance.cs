using System.Text.Json;
using System.Text.Json.Serialization;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Inovance;

public sealed record AcceptanceEvidenceFile(string Id, string Path, string Sha256);
public sealed record InovanceAcceptanceRecord(int SchemaVersion, string AcceptanceId, string ReviewedBy, DateTimeOffset ReviewedAt,
    string ReviewStatement, InovanceIdentity Identity, AcceptanceEvidenceFile[] Evidence, OperationAcceptance[] Grants);
public sealed record InovanceRunEvidence(int SchemaVersion, string ExecutionMode, bool Simulated, DateTimeOffset CapturedAt,
    InovanceIdentity Identity, JsonElement RuntimeIdentity, WorkerRequest Request, WorkerResponse Response);

/// <summary>Local operator attestation; hashes detect changes, not a signature or vendor certificate.
/// No MCP endpoint can write acceptance records. Every grant needs successful real invocation evidence.</summary>
public sealed class InovanceAcceptance(string? path, Func<InovanceIdentity> observe) : IExecutionAcceptance
{
    private readonly object gate = new();
    private string? loadedHash;
    private bool changed;
    private InovanceAcceptanceRecord? accepted;
    private static readonly JsonSerializerOptions Strict = new(WorkerJson.Options) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static readonly HashSet<string> ConditionFields = new(StringComparer.Ordinal)
    {
        "objectId", "applicationId", "edit.parentId", "edit.objectId", "edit.language", "edit.pouType", "edit.dutType",
        "edit.configuration.member.kind", "edit.configuration.member.language", "edit.configuration.member.expectedTypeId",
        "edit.configuration.device.identification.id", "edit.configuration.device.identification.version", "edit.configuration.device.identification.type",
        "edit.exchange.artifactId", "edit.exchange.format", "query.memberKind", "query.expectedTypeId", "exchange.format"
    };
    private static OperationError Denied(string code, string operation) => new(code, $"Operation {operation} is outside the current reviewed acceptance. Inspect plc_runtime_get_status / plc_capabilities_list.");
    public RuntimeReadiness GetReadiness()
    {
        lock (gate)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path)) return new("unverified", null, null, [], "acceptance_required");
                if (!Path.IsPathFullyQualified(path)) throw new InvalidOperationException("acceptance_path_invalid");
                var bytes = ReadBounded(path, 262144);
                var hash = InovanceIdentity.Hash(bytes);
                if (loadedHash is not null && loadedHash != hash) changed = true;
                if (changed) throw new InvalidOperationException("acceptance_changed_restart_required");
                var current = observe();
                var record = accepted ?? JsonSerializer.Deserialize<InovanceAcceptanceRecord>(bytes, Strict) ?? throw new InvalidOperationException("acceptance_invalid");
                Validate(record, path);
                if (record.Identity.Fingerprint() != current.Fingerprint()) throw new InvalidOperationException("acceptance_identity_mismatch");
                accepted = record; loadedHash = hash;
                return new("accepted", record.AcceptanceId, current.Fingerprint(), record.Grants, null, IdeVersion: current.Executable.FileVersion, EngineVersion: current.Engine.FileVersion, Profile: current.Profile, StrategyId: current.StrategyId);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or NullReferenceException or KeyNotFoundException or FormatException)
            {
                var code = error is InvalidOperationException && error.Message.StartsWith("acceptance_", StringComparison.Ordinal) ? error.Message : "acceptance_invalid";
                return new("unavailable", accepted?.AcceptanceId, null, [], code);
            }
        }
    }
    public OperationError? CheckOperation(string operation) => AcceptanceRules.Check(GetReadiness(), operation);
    public bool HasUnrestrictedGrant(string operation) => AcceptanceRules.Unrestricted(GetReadiness(), operation);
    public OperationError? CheckInput(string operation, JsonElement input) => AcceptanceRules.Check(GetReadiness(), operation, input);
    public static bool Matches(OperationAcceptance grant, JsonElement input) => AcceptanceRules.Matches(grant, input);
    public static byte[] ReadBounded(string path, int limit)
    {
        using var file = File.OpenRead(path);
        if (file.Length > limit) throw new InvalidOperationException("acceptance_file_too_large");
        var bytes = new byte[limit + 1];
        var count = 0;
        while (count < bytes.Length)
        {
            var read = file.Read(bytes, count, bytes.Length - count);
            if (read == 0) break;
            count += read;
        }
        if (count > limit) throw new InvalidOperationException("acceptance_file_too_large");
        return bytes[..count];
    }
    public static void Validate(InovanceAcceptanceRecord record, string manifestPath)
    {
        if (record.SchemaVersion != 1 || string.IsNullOrWhiteSpace(record.AcceptanceId) || record.AcceptanceId.Length > 128
            || string.IsNullOrWhiteSpace(record.ReviewedBy) || record.ReviewedBy.Length > 256
            || record.ReviewedAt == default || record.ReviewedAt > DateTimeOffset.UtcNow.AddMinutes(5)
            || record.ReviewStatement is not { Length: > 15 and <= 4096 } || record.Identity is null
            || record.Grants is not { Length: > 0 and <= 128 } || record.Evidence is not { Length: > 0 and <= 256 }
            || record.Evidence.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != record.Evidence.Length)
            throw new InvalidOperationException("acceptance_invalid");
        var evidence = new Dictionary<string, InovanceRunEvidence>(StringComparer.Ordinal);
        foreach (var reference in record.Evidence)
        {
            if (string.IsNullOrWhiteSpace(reference.Id) || Path.IsPathFullyQualified(reference.Path) || reference.Path.Split('/', '\\').Any(x => x is ".." or ".")) throw new InvalidOperationException("acceptance_evidence_invalid");
            var bytes = ReadBounded(Path.Combine(Path.GetDirectoryName(manifestPath)!, reference.Path), 2 * 1024 * 1024);
            if (InovanceIdentity.Hash(bytes) != reference.Sha256) throw new InvalidOperationException("acceptance_evidence_changed");
            var run = JsonSerializer.Deserialize<InovanceRunEvidence>(bytes, Strict) ?? throw new InvalidOperationException("acceptance_evidence_invalid");
            if (run.SchemaVersion != 1 || run.ExecutionMode != "real" || run.Simulated || run.CapturedAt > record.ReviewedAt
                || run.CapturedAt < DateTimeOffset.UnixEpoch || ContainsSimulationMarker(JsonSerializer.SerializeToElement(run, WorkerJson.Options))
                || run.Request.ExecutionMode != "real" || run.Response.ExecutionMode != "real" || run.Response.Outcome != "succeeded"
                || !WorkerJson.Matches(run.Request, run.Response) || !WorkerResultValidation.IsValid(run.Request, run.Response)
                || run.Identity.Fingerprint() != record.Identity.Fingerprint()
                || !RuntimeIdentityMatches(run.RuntimeIdentity, run.Identity)
                || run.Response.Payload is not { } response || !response.TryGetProperty("runtimeIdentity", out var runtime) || !RuntimeIdentityMatches(runtime, run.Identity)) throw new InvalidOperationException("acceptance_evidence_not_real_success");
            evidence.Add(reference.Id, run);
        }
        foreach (var grant in record.Grants)
        {
            if ((!EngineeringOperations.Contains(grant.Operation) && !EditOperations.Contains(grant.Operation) && grant.Operation != EditOperations.VerifyProject)
                || CompositeRequirements.Dependencies(grant.Operation).Length > 0
                || grant.Conditions is null || grant.Conditions.Count > 16 || grant.Conditions.Any(x => !ConditionFields.Contains(x.Key) || string.IsNullOrWhiteSpace(x.Value) || x.Value.Length > 256)
                || grant.Coverage is not ("operation" or "constrained") || (grant.Coverage == "operation") != (grant.Conditions.Count == 0)
                || grant.EvidenceIds is not { Length: > 0 and <= 64 }
                || grant.EvidenceIds.Any(id => !evidence.TryGetValue(id, out var run) || run.Request.Operation != grant.Operation || !Matches(grant, run.Request.Payload)))
                throw new InvalidOperationException("acceptance_grant_invalid");
        }
    }
    private static bool ContainsSimulationMarker(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array) return value.EnumerateArray().Any(ContainsSimulationMarker);
        if (value.ValueKind != JsonValueKind.Object) return false;
        return value.EnumerateObject().Any(p => p.Name == "simulated" && p.Value.ValueKind == JsonValueKind.True
            || p.Name == "executionMode" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "fixture"
            || p.Name == "evidenceKind" && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == "simulated"
            || ContainsSimulationMarker(p.Value));
    }
    public static bool RuntimeIdentityMatches(JsonElement actual, InovanceIdentity identity) => actual.ValueKind == JsonValueKind.Object
        && actual.TryGetProperty("executableSha256", out var exe) && exe.GetString() == identity.Executable.Sha256
        && actual.TryGetProperty("engineSha256", out var engine) && engine.GetString() == identity.Engine.Sha256
        && actual.TryGetProperty("status", out var status) && status.GetString() == "matched";
}
