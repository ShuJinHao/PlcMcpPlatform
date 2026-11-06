using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Incremental;

/// <summary>Original application logic over the existing vendor read/write ports. Runs only on a private candidate.</summary>
public sealed class IncrementalEditExecutor(IEngineeringExecutor worker) : IEngineeringExecutor
{
    public string ExecutionMode => worker.ExecutionMode;
    public OperationError? Unavailability => worker.Unavailability;
    public OperationError? CheckAvailability(string operation) => worker.CheckAvailability(operation);
    public ValidationEvidence? Validation => worker.Validation;
    public async Task<WorkerResponse> ExecuteAsync(WorkerRequest request, CancellationToken token)
    {
        if (request.Operation is not (IncrementalOperations.Patch or IncrementalOperations.Restore)) return await worker.ExecuteAsync(request, token);
        var session = new WorkerSession(worker, request, request.ExpectedRevision);
        try
        {
            if (CheckAvailability(request.Operation) is { } denied) throw new ProjectEditException(denied.Code);
            var input = request.Payload.Deserialize<EngineeringInput>(WorkerJson.Options)!;
            var edit = input.Edit ?? throw new ProjectEditException("invalid_request");
            if (!input.Candidate) throw new ProjectEditException("managed_candidate_required");
            JsonElement result;
            if (request.Operation == IncrementalOperations.Patch)
            {
                if (IncrementalValidation.Patch(edit) is { } invalid) throw new ProjectEditException(invalid);
                var source = await session.Invoke(IncrementalOperations.Source, new(input.ProjectPath, ObjectId: edit.ObjectId, IdeProfile: input.IdeProfile), token);
                var kind = source.GetProperty("kind").GetString();
                if (kind is not ("pou" or "gvl" or "dut")) throw new ProjectEditException("patch_object_kind_not_supported");
                var patch = edit.Patch!;
                if (patch.Section == "implementation" && (kind != "pou" || source.GetProperty("implementationKind").GetString() != "textual"))
                    throw new ProjectEditException("graphical_content_requires_specialized_editor");
                var text = source.GetProperty(patch.Section).GetString() ?? throw new ProjectEditException("textual_source_unavailable");
                var applied = TextPatchRules.Apply(text, patch.ExpectedSha256, patch.OldText, patch.NewText);
                if (applied.Error is { } conflict) throw new ProjectEditException(conflict);
                var replacement = new EditInput(edit.WorkspaceName, ObjectId: edit.ObjectId,
                    Declaration: patch.Section == "declaration" ? applied.Source : null, Implementation: patch.Section == "implementation" ? applied.Source : null);
                result = await session.Invoke(kind + ".update", input with { Edit = replacement }, token);
                var enriched = JsonNode.Parse(result.GetRawText())!.AsObject();
                enriched["patch"] = JsonSerializer.SerializeToNode(new { section = patch.Section, beforeSha256 = TextPatchRules.Hash(text), afterSha256 = TextPatchRules.Hash(applied.Source!) }, WorkerJson.Options);
                result = JsonSerializer.SerializeToElement(enriched, WorkerJson.Options);
            }
            else
            {
                result = await session.Invoke(EditOperations.VerifyProject, input with { Edit = new(edit.WorkspaceName) }, token);
                session.Revision = result.GetProperty("revision").GetString()!;
                var tree = await session.Invoke(EngineeringOperations.Structure, new(input.ProjectPath, MaxObjects: 2000, IdeProfile: input.IdeProfile), token);
                if (tree.GetProperty("truncated").GetBoolean()) throw new ProjectEditException("restore_scope_incomplete");
                var applications = tree.GetProperty("objects").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == "application").ToArray();
                if (applications.Length is < 1 or > 16) throw new ProjectEditException("restore_application_scope_invalid");
                var checks = new List<object>();
                foreach (var app in applications)
                {
                    var id = app.GetProperty("id").GetString()!;
                    var tasks = await session.Invoke(ConfigurationOperations.TaskList, new(input.ProjectPath, ObjectId: id, MaxObjects: 2000, IdeProfile: input.IdeProfile), token);
                    if (tasks.GetProperty("configuration").GetProperty("data").GetProperty("truncated").GetBoolean()) throw new ProjectEditException("task_inventory_incomplete");
                    var compile = await session.Invoke(EngineeringOperations.Compile, new(input.ProjectPath, ApplicationId: id, IdeProfile: input.IdeProfile), token);
                    checks.Add(new { applicationId = id, tasks, compile });
                }
                var enriched = JsonNode.Parse(result.GetRawText())!.AsObject();
                enriched["restore"] = JsonSerializer.SerializeToNode(new { sourceRevision = edit.RestoreRevision, checks }, WorkerJson.Options);
                result = JsonSerializer.SerializeToElement(enriched, WorkerJson.Options);
            }
            return new(1, request.RequestId, request.JobId, "succeeded", null, ExecutionMode, result);
        }
        catch (Exception error) { return session.Failure(error); }
    }
}
