using PlcMcp.Services.Contracts.Incremental;
using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.EngineeringService.Exchange;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Batch;

public static class BatchValidation
{
    public static string Canonical(string value) => Guid.TryParse(value, out var id) ? id.ToString() : value;
    public static EditInput Resolve(string workspace, BatchStep step, IReadOnlyDictionary<string, string> aliases)
    {
        string? Id(string? value) => value?.StartsWith('@') == true
            ? aliases.TryGetValue(value[1..], out var found) ? found : throw new ProjectEditException("batch_reference_not_ready")
            : value is null ? null : Canonical(value);
        var c = step.Change;
        var config = c.Configuration is { } settings ? settings with { Calls = settings.Calls?.Select(call => call with { PouId = Id(call.PouId)! }).ToArray() } : null;
        return new(workspace, ParentId: Id(c.ParentId), ObjectId: Id(c.ObjectId), Name: c.Name, Declaration: c.Declaration,
            Implementation: c.Implementation, PouType: c.PouType, Language: c.Language, DutType: c.DutType, BaseType: c.BaseType,
            Configuration: config, Exchange: c.ArtifactId is null ? null : new(c.ArtifactId, "plcopen", null,
                c.ObjectId is null ? "reject" : "replace", false, c.ExpectedGraphSha256), Patch: c.Patch);
    }
    public static string? Validate(string workspace, BatchPlan? plan)
    {
        if (plan is null || !Guid.TryParse(plan.ApplicationId, out var app) || app == Guid.Empty || plan.Steps is not { Length: > 0 and <= 32 }) return "invalid_batch_plan";
        foreach (var scope in new[] { plan.DeviceScopeIds, plan.LibraryManagerIds })
            if (scope is not null && (scope.Length > 32 || scope.Any(x => !Guid.TryParse(x, out var id) || id == Guid.Empty)
                || scope.Select(Guid.Parse).Distinct().Count() != scope.Length)) return "invalid_batch_scope";
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var step in plan.Steps)
            {
                if (step is null || step.Change is null || !GraphRules.Identifier(step.Id) || step.Id.Length > 48
                    || aliases.ContainsKey(step.Id) || !BatchOperations.Steps.Contains(step.Operation)) return "invalid_batch_step";
                if (step.Change.Configuration?.Calls?.Any(c => c is null) == true) return "invalid_task_calls";
                if (step.Operation != ExchangeOperations.WriteGraph && (step.Change.ArtifactId is not null || step.Change.ExpectedGraphSha256 is not null)) return "batch_field_not_applicable";
                var input = Resolve(workspace, step, aliases);
                var error = ManagedEditingJobs.Validate(step.Operation, input, "batch-validation", "revision", 600);
                if (error is not null) return error;
                // Unique deterministic placeholders validate references without inventing vendor identities.
                aliases.Add(step.Id, new Guid(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(step.Id))[..16]).ToString());
            }
        }
        catch (ProjectEditException e) { return e.Code; }
        return null;
    }
    public static BatchPlan Normalize(BatchPlan plan) => plan with { ApplicationId = Canonical(plan.ApplicationId),
        DeviceScopeIds = plan.DeviceScopeIds?.Select(Canonical).Order(StringComparer.Ordinal).ToArray(),
        LibraryManagerIds = plan.LibraryManagerIds?.Select(Canonical).Order(StringComparer.Ordinal).ToArray(), Steps = plan.Steps.Select(s => s with
    { Change = s.Change with { ParentId = s.Change.ParentId is { } p ? Canonical(p) : null, ObjectId = s.Change.ObjectId is { } o ? Canonical(o) : null,
        Configuration = s.Change.Configuration is { } c ? c with { Calls = c.Calls?.Select(x => x with { PouId = Canonical(x.PouId) }).ToArray() } : null } }).ToArray() };
    public static string Hash(string workspace, string revision, BatchPlan plan) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new { workspace, revision, plan }, WorkerJson.Options))).ToLowerInvariant();
    public static string[] Required(BatchPlan plan) => new[] { BatchOperations.Apply, EditOperations.VerifyProject, EngineeringOperations.Structure, EngineeringOperations.Compile, ConfigurationOperations.TaskList }
        .Concat(plan.Steps.Select(s => s.Operation)).Concat(plan.Steps.Where(s => s.Operation == IncrementalOperations.Patch).SelectMany(s => s.Change.Patch?.MemberKind is not null
            ? new[] { ConfigurationOperations.MemberRead, ConfigurationOperations.MemberUpdate }
            : new[] { IncrementalOperations.Source, EditOperations.UpdatePou, EditOperations.UpdateGvl, EditOperations.UpdateDut })).Concat(plan.Steps.Any(s => s.Operation == ExchangeOperations.WriteGraph && s.Change.ObjectId is not null) ? [ExchangeOperations.ReadGraph] : [])
        .Distinct(StringComparer.Ordinal).ToArray();
}

public sealed class BatchService(ManagedEditingJobs edits, IExchangeArtifacts artifacts, IPlcGraphPolicy? targetPolicy = null)
{
    private async Task<BatchPlan> Prepare(string workspace, BatchPlan plan, CancellationToken token)
    {
        if (BatchValidation.Validate(workspace, plan) is { } invalid) throw new ProjectEditException(invalid);
        var normalized = BatchValidation.Normalize(plan);
        foreach (var step in normalized.Steps.Where(s => s.Operation == ExchangeOperations.WriteGraph))
        {
            var artifact = await artifacts.GetAsync(step.Change.ArtifactId!, token);
            if (artifact.Artifact.Format != "plcopen") throw new ProjectEditException("graph_name_mismatch");
            var graph = PlcOpenGraphCodec.Decode(artifact.Xml).Program;
            if (graph.Name != step.Change.Name) throw new ProjectEditException("graph_name_mismatch");
            if (targetPolicy?.Validate(graph) is { } invalidGraph) throw new ProjectEditException(invalidGraph);
        }
        return normalized;
    }
    public async Task<BatchPreview> PreviewAsync(string workspace, string revision, BatchPlan plan, CancellationToken token)
    {
        var normalized = await Prepare(workspace, plan, token);
        var current = await edits.GetWorkspaceAsync(workspace, token);
        if (!current.IsSuccess) throw new ProjectEditException(current.Error!.Code);
        if (current.Value!.Revision != revision) throw new ProjectEditException("workspace_revision_conflict");
        return new(workspace, revision, BatchValidation.Hash(workspace, revision, normalized), normalized.ApplicationId, normalized.Steps, BatchValidation.Required(normalized),
            ["Static preview is not IDE validation. Steps execute in the supplied dependency order.",
             "One candidate and one publication; each step uses the existing save/reopen lifecycle.",
             "Shared device/library changes compile every Application; other steps remain in the selected Application.",
             "Renames/moves/deletions do not rewrite source references. Submit explicit dependent edits.",
             "Compilation must succeed. Unknown execution blocks the slot and is never automatically replayed."], normalized.DeviceScopeIds, normalized.LibraryManagerIds);
    }
    public async Task<Result<JobView>> ApplyAsync(string workspace, string revision, BatchPlan plan, string expectedPlanSha256, string key, int timeout, CancellationToken token)
    {
        try
        {
            var normalized = await Prepare(workspace, plan, token);
            if (BatchValidation.Hash(workspace, revision, normalized) != expectedPlanSha256) return Result<JobView>.Failure("batch_plan_changed", "Preview the exact plan and base revision before submission.");
            return await edits.SubmitAsync(BatchOperations.Apply, new(workspace, Batch: normalized, PlanSha256: expectedPlanSha256), key, revision, timeout, token);
        }
        catch (ProjectEditException e) { return Result<JobView>.Failure(e.Code, "Batch input could not be validated."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return Result<JobView>.Failure("engineering_data_unavailable", "Batch artifacts are unavailable or invalid."); }
    }
}
