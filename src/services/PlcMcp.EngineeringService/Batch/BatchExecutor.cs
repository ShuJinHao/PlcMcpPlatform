using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Batch;

/// <summary>Runs inside the existing candidate coordinator. Never publishes intermediate steps or resumes an unknown write.</summary>
public sealed class BatchExecutor(IEngineeringExecutor worker, IBatchJournal journal) : IEngineeringExecutor
{
    public string ExecutionMode => worker.ExecutionMode;
    public OperationError? Unavailability => worker.Unavailability;
    public ValidationEvidence? Validation => worker.Validation;
    public OperationError? CheckAvailability(string operation) => worker.CheckAvailability(operation);
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (request.Operation != BatchOperations.Apply) return await worker.ExecuteAsync(request, token);
        var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
        if (!input.Candidate || input.Edit?.Batch is not { } plan) return WorkerJson.Failed(request, "managed_candidate_required");
        if (BatchValidation.Validate(input.Edit.WorkspaceName, plan) is { } invalid) return WorkerJson.Failed(request, invalid);
        foreach (var operation in BatchValidation.Required(plan))
            if (worker.CheckAvailability(operation) is { } unavailable) return WorkerJson.Failed(request, unavailable.Code);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal); var steps = new List<BatchStepResult>();
        var revision = request.ExpectedRevision; var dependencies = input.CurrentDependencies;
        string? active = null; var executing = false;
        async Task Record(string state, string? error = null) => await journal.WriteAsync(new(request.JobId, state, active, steps.ToArray(), error), CancellationToken.None);
        WorkerResponse Failure(string outcome, string code, JsonElement? details = null) => new(1, request.RequestId, request.JobId, outcome, code, request.ExecutionMode,
            JsonSerializer.SerializeToElement(new { published = false, batch = new { state = outcome, activeStep = active, steps, aliases, details } }, WorkerJson.Options));
        async Task<WorkerResponse> Invoke(string operation, EngineeringInput child)
        {
            token.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= request.DeadlineUtc) throw new ProjectEditException("deadline_expired");
            await Record("executing"); executing = true;
            var call = request with { RequestId = Guid.NewGuid(), Operation = operation, ExpectedRevision = revision,
                Payload = JsonSerializer.SerializeToElement(child with { CurrentDependencies = dependencies }, WorkerJson.Options) };
            var result = await worker.ExecuteAsync(call, token);
            if (!WorkerJson.Matches(call, result) || !WorkerResultValidation.IsValid(call, result)) return WorkerJson.Unknown(call, "worker_protocol_error");
            executing = result.Outcome == "unknown";
            return result;
        }
        try
        {
            active = "$structure";
            var tree = await Invoke(EngineeringOperations.Structure, new(input.ProjectPath, MaxObjects: 2000, IdeProfile: input.IdeProfile));
            if (tree.Outcome != "succeeded") { await Record(tree.Outcome, tree.ErrorCode); return Failure(tree.Outcome, tree.ErrorCode!, tree.Payload); }
            var structure = tree.Payload!.Value;
            if (structure.GetProperty("truncated").GetBoolean()) throw new ProjectEditException("batch_scope_incomplete");
            var rows = structure.GetProperty("objects").EnumerateArray().ToArray();
            if (!rows.Any(r => r.GetProperty("id").GetString() == plan.ApplicationId && r.GetProperty("kind").GetString() == "application")) throw new ProjectEditException("application_required");
            var scope = new BatchScope(plan, rows);
            scope.ValidateDeclaredScopes();
            foreach (var step in plan.Steps)
            {
                active = step.Id;
                var edit = BatchValidation.Resolve(input.Edit.WorkspaceName, step, aliases);
                scope.ValidateStep(step.Operation, edit);
                var response = await Invoke(step.Operation, input with { Edit = edit, Exchange = null });
                if (response.Outcome != "succeeded")
                {
                    steps.Add(new(step.Id, step.Operation, response.Outcome, null, response.ErrorCode, null, response.Payload));
                    await Record(response.Outcome, response.ErrorCode); return Failure(response.Outcome, response.ErrorCode!);
                }
                var result = response.Payload!.Value;
                revision = result.GetProperty("revision").GetString()!;
                if (result.TryGetProperty("dependencySnapshot", out var deps)) dependencies = deps.Deserialize<DependencySnapshot>(WorkerJson.Options);
                var id = result.TryGetProperty("objectId", out var objectId) ? objectId.GetString()
                    : result.TryGetProperty("configuration", out var configuration) ? configuration.GetProperty("objectId").GetString()
                    : result.GetProperty("exchange").GetProperty("objectIds")[0].GetString();
                if (!Guid.TryParse(id, out var identity) || identity == Guid.Empty)
                { await Record("unknown", "batch_object_identity_unconfirmed"); return Failure("unknown", "batch_object_identity_unconfirmed"); }
                aliases.Add(step.Id, identity.ToString());
                JsonElement? detail = result.TryGetProperty("configuration", out var config) ? config : result.TryGetProperty("exchange", out var exchange)
                    ? JsonSerializer.SerializeToElement(new { artifact = exchange.GetProperty("artifact"), semanticSha256 = exchange.GetProperty("graph").GetProperty("semanticSha256"), report = exchange.GetProperty("report") }, WorkerJson.Options)
                    : result.TryGetProperty("patch", out var patch) ? patch : null;
                steps.Add(new(step.Id, step.Operation, "succeeded", id, null, revision, detail)); await Record("step_completed");
                // Rebuild scope after every write: moved/deleted descendants and vendor-created children must not use stale identities.
                var updated = await Invoke(EngineeringOperations.Structure, new(input.ProjectPath, MaxObjects: 2000, IdeProfile: input.IdeProfile));
                if (updated.Outcome != "succeeded") { await Record(updated.Outcome, updated.ErrorCode); return Failure(updated.Outcome, updated.ErrorCode!, updated.Payload); }
                if (updated.Payload!.Value.GetProperty("truncated").GetBoolean()) throw new ProjectEditException("batch_scope_incomplete");
                scope = new BatchScope(plan, updated.Payload.Value.GetProperty("objects").EnumerateArray().ToArray());
            }
            active = "$verify";
            var verified = await Invoke(EditOperations.VerifyProject, input with { Edit = new(input.Edit.WorkspaceName) });
            if (verified.Outcome != "succeeded") { await Record(verified.Outcome, verified.ErrorCode); return Failure(verified.Outcome, verified.ErrorCode!, verified.Payload); }
            revision = verified.Payload!.Value.GetProperty("revision").GetString()!;
            dependencies = verified.Payload.Value.GetProperty("dependencySnapshot").Deserialize<DependencySnapshot>(WorkerJson.Options);
            var applicationIds = scope.Applications(BatchOperations.SharedWrite(plan));
            var checks = new List<object>();
            WorkerResponse? selectedTasks = null, selectedCompile = null;
            foreach (var applicationId in applicationIds)
            {
                active = "$tasks:" + applicationId;
                var tasks = await Invoke(ConfigurationOperations.TaskList, new(input.ProjectPath, ObjectId: applicationId, IdeProfile: input.IdeProfile, MaxObjects: 2000));
                if (tasks.Outcome != "succeeded") { await Record(tasks.Outcome, tasks.ErrorCode); return Failure(tasks.Outcome, tasks.ErrorCode!, tasks.Payload); }
                if (tasks.Payload!.Value.GetProperty("configuration").GetProperty("data").GetProperty("truncated").GetBoolean()) throw new ProjectEditException("task_inventory_incomplete");
                if (!ConfigurationResultValidation.CompleteTaskBindings(tasks.Payload.Value)) throw new ProjectEditException("task_binding_unresolved");
                active = "$compile:" + applicationId;
                var compiled = await Invoke(EngineeringOperations.Compile, new(input.ProjectPath, ApplicationId: applicationId, IdeProfile: input.IdeProfile));
                if (compiled.Outcome != "succeeded") { await Record(compiled.Outcome, compiled.ErrorCode); return Failure(compiled.Outcome, compiled.ErrorCode!, compiled.Payload); }
                checks.Add(new { applicationId, tasks = tasks.Payload, compile = compiled.Payload });
                if (applicationId == plan.ApplicationId) { selectedTasks = tasks; selectedCompile = compiled; }
            }
            var payload = JsonNode.Parse(verified.Payload.Value.GetRawText())!.AsObject();
            payload["baseRevision"] = request.ExpectedRevision;
            payload["batch"] = JsonSerializer.SerializeToNode(new { state = "verified", steps, aliases, applicationId = plan.ApplicationId, planSha256 = input.Edit.PlanSha256,
                compile = selectedCompile!.Payload, tasks = selectedTasks!.Payload, checks, affectedApplicationIds = applicationIds,
                compileScope = BatchOperations.SharedWrite(plan) ? "all_applications" : "selected_application", verifiedProjectSha256 = revision }, WorkerJson.Options);
            await Record("verified");
            return new(1, request.RequestId, request.JobId, "succeeded", null, request.ExecutionMode, JsonSerializer.SerializeToElement(payload, WorkerJson.Options));
        }
        catch (Exception e) when (e is ProjectEditException or OperationCanceledException or IOException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            var code = e is ProjectEditException known ? known.Code : e is OperationCanceledException ? "batch_cancelled" : "batch_execution_failed";
            var outcome = executing ? "unknown" : e is OperationCanceledException ? "cancelled" : "failed";
            await Record(outcome, code); return Failure(outcome, code);
        }
    }
}
