using System.Text.Json;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Services.Contracts.Execution;

public static class PlatformRelease
{
    public const string Stage = "P10-delivery-closure", ContractVersion = "1.9";
}
// Conditions are exact, case-sensitive matches on documented request fields, not executable expressions.
public sealed record OperationAcceptance(string Operation, string Coverage, Dictionary<string, string> Conditions, string[] EvidenceIds);
public sealed record RuntimeReadiness(string State, string? AcceptanceId, string? IdentitySha256,
    OperationAcceptance[] Grants, string? ErrorCode, string Scope = "accepted_operations_and_conditions_only", string? IdeVersion = null, string? EngineVersion = null, string? Profile = null, string? StrategyId = null);
public interface IExecutionAcceptance
{
    RuntimeReadiness GetReadiness();
    OperationError? CheckOperation(string operation);
    OperationError? CheckInput(string operation, JsonElement input);
    bool HasUnrestrictedGrant(string operation);
}

public static class AcceptanceRules
{
    public static bool Unrestricted(RuntimeReadiness state, string operation) => state.State == "accepted" && state.Grants.Any(g => g.Operation == operation && g.Coverage == "operation");
    public static OperationError? Check(RuntimeReadiness state, string operation)
    {
        string? code = state.State != "accepted" ? state.ErrorCode ?? "acceptance_required" : null;
        if (code is null)
        {
            var dependencies = CompositeRequirements.Dependencies(operation);
            code = dependencies.Length > 0 ? dependencies.All(op => Unrestricted(state, op)) ? null : "dependency_scope_not_accepted"
                : state.Grants.Any(g => g.Operation == operation) ? null : "operation_not_accepted";
        }
        if (code is null && operation == PlcMcp.Services.Contracts.Batch.BatchOperations.Apply
            && !state.Grants.Any(g => PlcMcp.Services.Contracts.Batch.BatchOperations.Steps.Contains(g.Operation))) code = "operation_not_accepted";
        return code is null ? null : new(code, $"Operation {operation} is outside the current reviewed acceptance; inspect runtime status and acceptedScopes.");
    }
    public static bool Matches(OperationAcceptance grant, JsonElement input) => grant.Conditions.All(condition =>
    {
        var value = input;
        foreach (var part in condition.Key.Split('.'))
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return false;
        return value.ValueKind is JsonValueKind.String && value.GetString() == condition.Value
            || value.ValueKind is JsonValueKind.Number && value.GetRawText() == condition.Value;
    });
    public static OperationError? Check(RuntimeReadiness state, string operation, JsonElement input)
    {
        if (Check(state, operation) is { } denied) return denied;
        return CompositeRequirements.Dependencies(operation).Length > 0 || state.Grants.Any(g => g.Operation == operation && Matches(g, input))
            ? null : new("operation_scope_not_accepted", $"Parameters for {operation} are outside the accepted conditions.");
    }
}
