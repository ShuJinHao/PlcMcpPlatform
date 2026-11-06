using Microsoft.EntityFrameworkCore;
using PlcMcp.Core.Automation;
using PlcMcp.Core.Workspaces;

namespace PlcMcp.EntityFrameworkCore;

/// <summary>平台元数据与持久化执行记录；不保存 PLC 工程对象副本。</summary>
public sealed class PlcMcpDbContext(DbContextOptions<PlcMcpDbContext> options) : DbContext(options)
{
    public DbSet<ProjectWorkspace> Workspaces => Set<ProjectWorkspace>();
    public DbSet<AutomationJob> Jobs => Set<AutomationJob>();
    public DbSet<JobRecord> JobRecords => Set<JobRecord>();
    public DbSet<ExecutionState> ExecutionStates => Set<ExecutionState>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var workspace = modelBuilder.Entity<ProjectWorkspace>();
        workspace.HasKey(x => x.Id);
        workspace.Property(x => x.Version).IsConcurrencyToken();
        workspace.Property(x => x.Revision).IsRequired();
        workspace.Property(x => x.ProjectPath).IsRequired();
        workspace.HasIndex(x => x.ProjectPath).IsUnique();
        var job = modelBuilder.Entity<AutomationJob>();
        job.HasKey(x => x.Id);
        job.Property(x => x.Version).IsConcurrencyToken();
        job.Property(x => x.Status).HasConversion<string>();
        job.Property(x => x.CreatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        job.Property(x => x.UpdatedAt).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        job.HasIndex(x => new { x.WorkspaceId, x.Operation, x.IdempotencyKey }).IsUnique();
        job.HasOne<ProjectWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<JobRecord>().HasKey(x => x.JobId);
        modelBuilder.Entity<JobRecord>().HasOne<AutomationJob>().WithOne().HasForeignKey<JobRecord>(x => x.JobId);
        modelBuilder.Entity<ExecutionState>().HasKey(x => x.Id);
    }
}
