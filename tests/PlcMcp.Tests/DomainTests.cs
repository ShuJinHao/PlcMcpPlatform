using PlcMcp.Core.Automation;
using PlcMcp.Core.Workspaces;
using Xunit;

namespace PlcMcp.Tests;

public sealed class DomainTests
{
    private static AutomationJob Job() => AutomationJob.Queue(Guid.NewGuid(), "pou.update", "key", "hash", DateTimeOffset.UnixEpoch);
    [Fact] public void Queued_job_cannot_succeed_without_execution()
        => Assert.Throws<InvalidOperationException>(() => Job().CompleteVerified(DateTimeOffset.UtcNow));
    [Fact] public void Running_cancellation_requires_executor_confirmation()
    {
        var job = Job(); job.Start(DateTimeOffset.UtcNow); job.RequestCancellation(DateTimeOffset.UtcNow);
        Assert.Equal(JobStatus.CancelRequested, job.Status);
        job.ConfirmCancellation(DateTimeOffset.UtcNow);
        Assert.Equal(JobStatus.Cancelled, job.Status);
    }
    [Fact] public void Unknown_outcome_cannot_be_restarted()
    {
        var job = Job(); job.Start(DateTimeOffset.UtcNow); job.MarkOutcomeUnknown(DateTimeOffset.UtcNow);
        Assert.Throws<InvalidOperationException>(() => job.Start(DateTimeOffset.UtcNow));
    }
    [Fact] public void Completion_can_win_race_with_cancellation_request()
    {
        var job = Job(); job.Start(DateTimeOffset.UtcNow); job.RequestCancellation(DateTimeOffset.UtcNow);
        job.CompleteVerified(DateTimeOffset.UtcNow);
        Assert.Equal(JobStatus.Succeeded, job.Status);
    }
    [Fact] public void Stale_revision_cannot_replace_workspace_revision()
    {
        var workspace = ProjectWorkspace.Register("inovance", Path.GetFullPath("example.project"), "r1");
        Assert.Throws<InvalidOperationException>(() => workspace.RecordVerifiedRevision("r0", "r2"));
        Assert.Equal("r1", workspace.Revision);
        workspace.RecordVerifiedRevision("r1", "r2");
        Assert.Equal(1, workspace.Version);
    }
}
