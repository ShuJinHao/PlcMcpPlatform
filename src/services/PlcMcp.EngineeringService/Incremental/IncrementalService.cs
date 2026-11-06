using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.EngineeringService.Incremental;

public sealed class IncrementalService(IJobStore jobs, IManagedProjectFiles files, IProjectChangeStore changes,
    IProjectHistoryStore history, IEngineeringExecutor executor)
{
    public async Task<AdoptionPreview> PreviewAdoptionAsync(AdoptionSource source, CancellationToken token)
    {
        var snapshot = await files.ReadAdoptionAsync(source, executor.ExecutionMode, token);
        await files.VerifyAsync(snapshot, token);
        return new(snapshot.Revision, snapshot.Files, snapshot.Template);
    }
    public async Task<HistoryPage> HistoryAsync(string workspace, string revision, int offset, int pageSize, CancellationToken token)
    {
        if (offset is < 0 or > 1000000 || pageSize is < 1 or > 64) throw new ProjectEditException("invalid_page");
        var path = files.WorkspacePath(workspace);
        var head = await Head(path, revision, token);
        var records = await history.ListAsync(path, offset, pageSize + 1, token);
        var entries = records.Take(pageSize).Select(x => new VersionView(x.Evidence.Response!.Payload!.Value.GetProperty("revision").GetString()!,
            x.Evidence.JobId, x.Operation, x.CreatedAt, x.Evidence.BaseRevision, x.Evidence.JobId == head.JobId)).ToArray();
        _ = await Head(path, revision, token);
        return new(workspace, revision, entries, records.Length > pageSize ? offset + pageSize : null);
    }
    public async Task<Result<JobView>> ReadAsync(string operation, string headRevision, IncrementalQuery query, string key, int timeout, CancellationToken token)
    {
        try
        {
            if (operation is not (IncrementalOperations.Context or IncrementalOperations.Diff or IncrementalOperations.Query) || !IncrementalValidation.Revision(headRevision)
                || !IncrementalValidation.Revision(query.Revision) || operation == IncrementalOperations.Diff && !IncrementalValidation.Revision(query.CompareRevision)
                || (operation is IncrementalOperations.Context or IncrementalOperations.Query || query.ObjectId is not null) && !IncrementalValidation.Id(query.ApplicationId)
                || query.CompareObjectId is not null && (query.ObjectId is null || !IncrementalValidation.Id(query.CompareObjectId))
                || query.ObjectId is not null && !IncrementalValidation.Id(query.ObjectId) || query.Graph && query.ObjectId is null
                || query.Offset is < 0 or > 1000000 || query.PageSize is < 1 or > 8 || query.TextOffset is < 0 or > 400000
                || query.MaxTextChars is < 1 or > 8192 || string.IsNullOrWhiteSpace(key) || key.Length > 128 || timeout is < 1 or > 600)
                throw new ProjectEditException("invalid_incremental_query");
            query = query with { ApplicationId = query.ApplicationId is null ? null : Guid.Parse(query.ApplicationId).ToString(), CompareObjectId = query.CompareObjectId is null ? null : Guid.Parse(query.CompareObjectId).ToString(), ObjectId = query.ObjectId is null ? null : Guid.Parse(query.ObjectId).ToString() };
            if (query.Collection is not ("objects" or "members" or "tasks" or "libraries")
                || operation != IncrementalOperations.Query && (query.Collection != "objects" || query.ParentId is not null || query.SourceQuery is not null || query.Cursor is not null)
                || query.ParentId is not null && !IncrementalValidation.Id(query.ParentId)
                || query.Collection == "members" && query.ParentId is null
                || query.Collection is "tasks" or "libraries" && (query.ParentId is not null || query.ObjectId is not null)
                || query.SourceQuery is not null && (query.ObjectId is null || Configuration.ConfigurationValidation.Read(
                    PlcMcp.Services.Contracts.Configuration.ConfigurationOperations.MemberRead, query.ObjectId, query.SourceQuery) is not null))
                throw new ProjectEditException("invalid_engineering_query");
            query = query with { ParentId = query.ParentId is null ? null : Guid.Parse(query.ParentId).ToString(),
                SourceQuery = query.SourceQuery is null ? null : query.SourceQuery with { ExpectedTypeId = query.SourceQuery.ExpectedTypeId is null ? null : Guid.Parse(query.SourceQuery.ExpectedTypeId).ToString() } };
            query = EngineeringCursor.Resume(headRevision, query);
            var path = files.WorkspacePath(query.WorkspaceName);
            var submission = new Submission(operation, new(path, Incremental: query), key, headRevision, timeout);
            var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(submission, WorkerJson.Options)));
            var prior = await jobs.FindSubmissionAsync(path, operation, key, fingerprint, token);
            if (prior is not null) return prior;
            foreach (var op in new[] { operation }.Concat(IncrementalValidation.Required(operation))
                .Concat(operation == IncrementalOperations.Diff && query.ObjectId is not null ? new[] { EngineeringOperations.Structure, query.Graph ? ExchangeOperations.ReadGraph : IncrementalOperations.Source } : []))
                if (executor.CheckAvailability(op) is { } denied) return Result<JobView>.Failure(denied.Code, denied.Message);
            var head = await Head(path, headRevision, token);
            foreach (var revision in new[] { query.Revision, query.CompareRevision }.OfType<string>())
                await files.ReadVersionAsync(await history.GetAsync(path, revision, token) ?? throw new ProjectEditException("version_not_found"), head.Snapshot.Template, token);
            return await jobs.SubmitAsync(submission, new(path, headRevision), fingerprint, executor.ExecutionMode, token);
        }
        catch (ProjectEditException e) { return Result<JobView>.Failure(e.Code, "Incremental query requires matching managed versions and bounded scope."); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return Result<JobView>.Failure("project_unavailable", "Version files or metadata are unavailable."); }
    }
    private async Task<ManagedHead> Head(string path, string revision, CancellationToken token)
    {
        var head = await changes.GetHeadAsync(path, token) ?? throw new ProjectEditException("workspace_not_found");
        if (head.Snapshot.Revision != revision) throw new ProjectEditException("workspace_revision_conflict");
        if (head.Snapshot.Template.ExecutionMode != executor.ExecutionMode) throw new ProjectEditException("execution_mode_mismatch");
        await files.VerifyAsync(head.Snapshot, token);
        return head;
    }
}
