using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlcMcp.Core.Automation;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Maintenance;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.EntityFrameworkCore;

/// <summary>Read-only maintenance snapshot. Never migrates, reconciles or alters jobs.</summary>
public sealed class SqliteRetentionState(DbContextOptions<PlcMcpDbContext> options) : IRetentionStateSource
{
    public async Task<RetentionState> ReadAsync(CancellationToken token)
    {
        await using var db = new PlcMcpDbContext(options);
        var slot = await db.ExecutionStates.AsNoTracking().SingleAsync(token);
        var jobs = await db.Jobs.AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(token);
        var heads = await db.ManagedProjects.AsNoTracking().OrderBy(x => x.WorkspacePath).ToArrayAsync(token);
        var changes = await db.ProjectChanges.AsNoTracking().OrderBy(x => x.JobId).ToArrayAsync(token);
        var digest = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { slot, jobs, heads, changes }, WorkerJson.Options))).ToLowerInvariant();
        var blocked = slot.Blocked || jobs.Any(j => j.Status is JobStatus.Queued or JobStatus.Running or JobStatus.CancelRequested or JobStatus.OutcomeUnknown);
        var failures = new List<RetentionFailure>();
        foreach (var row in changes)
        {
            var evidence = JsonSerializer.Deserialize<ChangeEvidence>(row.EvidenceJson, WorkerJson.Options) ?? throw new ProjectEditException("maintenance_invalid_evidence");
            var job = jobs.Single(j => j.Id == row.JobId);
            if (evidence.JobId != row.JobId || evidence.Stage != row.Stage || evidence.WorkspacePath != row.WorkspacePath) throw new ProjectEditException("maintenance_invalid_evidence");
            if (evidence.Stage is "preparing" or "prepared" or "executing" or "verified" or "unknown" || !evidence.ExecutorStopped) blocked = true;
            if (job.Status is JobStatus.Failed or JobStatus.Cancelled && evidence.Stage is "failed" or "cancelled" && evidence.ExecutorStopped)
                failures.Add(new(evidence, job.UpdatedAt));
        }
        var parsed = heads.Select(row =>
        {
            var head = JsonSerializer.Deserialize<ManagedHead>(row.HeadJson, WorkerJson.Options) ?? throw new ProjectEditException("maintenance_invalid_head");
            if (head.WorkspacePath != row.WorkspacePath || head.Snapshot.Revision != row.Revision) throw new ProjectEditException("maintenance_invalid_head");
            return head;
        }).ToArray();
        return new(digest, blocked, parsed, failures.ToArray());
    }
}
