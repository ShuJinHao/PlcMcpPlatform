using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlcMcp.Core.Workspaces;
using PlcMcp.Core.Automation;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.EntityFrameworkCore;

public sealed class SqliteProjectChangeStore(DbContextOptions<PlcMcpDbContext> options) : IProjectChangeStore
{
    public async Task<ManagedHead?> GetHeadAsync(string workspacePath, CancellationToken token)
    {
        await using var db = new PlcMcpDbContext(options);
        var row = await db.ManagedProjects.AsNoTracking().SingleOrDefaultAsync(x => x.WorkspacePath == workspacePath, token);
        return row is null ? null : JsonSerializer.Deserialize<ManagedHead>(row.HeadJson, WorkerJson.Options);
    }
    public async Task<ChangeEvidence?> GetChangeAsync(Guid jobId, CancellationToken token)
    {
        await using var db = new PlcMcpDbContext(options);
        var row = await db.ProjectChanges.AsNoTracking().SingleOrDefaultAsync(x => x.JobId == jobId, token);
        return row is null ? null : JsonSerializer.Deserialize<ChangeEvidence>(row.EvidenceJson, WorkerJson.Options);
    }
    public async Task RecordAsync(ChangeEvidence evidence, CancellationToken token, string? expectedStage = null)
    {
        await using var db = new PlcMcpDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var job = await db.Jobs.SingleAsync(x => x.Id == evidence.JobId, token);
        if (evidence.Stage is "preparing" or "prepared" or "executing" or "verified" && job.Status is not (JobStatus.Running or JobStatus.CancelRequested))
            throw new InvalidOperationException("job_execution_ownership_lost");
        var row = await db.ProjectChanges.SingleOrDefaultAsync(x => x.JobId == evidence.JobId, token);
        if (row is null) { row = new() { JobId = evidence.JobId, WorkspacePath = evidence.WorkspacePath }; db.ProjectChanges.Add(row); }
        if (expectedStage is not null && row.Stage != expectedStage) throw new ProjectEditException("reconciliation_raced");
        if (!ProjectChangeLifecycle.CanAdvance(row.Stage, evidence.Stage)) throw new InvalidOperationException("invalid_change_transition");
        row.Stage = evidence.Stage; row.EvidenceJson = JsonSerializer.Serialize(evidence, WorkerJson.Options);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
    }
    public async Task<bool> PublishAsync(ManagedHead head, ChangeEvidence evidence, bool creating, CancellationToken token)
    {
        if (evidence.Stage != "published" || !evidence.ExecutorStopped || evidence.Response?.Outcome != "succeeded")
            throw new InvalidOperationException("publication_requires_verified_execution");
        await using var db = new PlcMcpDbContext(options);
        await using var tx = await db.Database.BeginTransactionAsync(token);
        var job = await db.Jobs.SingleAsync(x => x.Id == head.JobId, token);
        if (job.Status is not (JobStatus.Running or JobStatus.CancelRequested)) return false;
        var current = await db.ManagedProjects.SingleOrDefaultAsync(x => x.WorkspacePath == head.WorkspacePath, token);
        if (creating ? current is not null : current?.Revision != evidence.BaseRevision) return false;
        if (current is null) { current = new() { WorkspacePath = head.WorkspacePath }; db.ManagedProjects.Add(current); }
        var workspace = await db.Workspaces.SingleAsync(x => x.ProjectPath == head.WorkspacePath, token);
        workspace.RecordVerifiedRevision(workspace.Revision, head.Snapshot.Revision);
        current.Revision = head.Snapshot.Revision; current.HeadJson = JsonSerializer.Serialize(head, WorkerJson.Options);
        var change = await db.ProjectChanges.SingleAsync(x => x.JobId == head.JobId, token);
        if (change.Stage != "verified") throw new InvalidOperationException("invalid_change_transition");
        change.Stage = evidence.Stage; change.EvidenceJson = JsonSerializer.Serialize(evidence, WorkerJson.Options);
        await db.SaveChangesAsync(token); await tx.CommitAsync(token);
        return true;
    }
}
