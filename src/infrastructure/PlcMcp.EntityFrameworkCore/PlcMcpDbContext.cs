using Microsoft.EntityFrameworkCore;
using PlcMcp.Core.Automation;
using PlcMcp.Core.Workspaces;

namespace PlcMcp.EntityFrameworkCore;

/// <summary>P0 映射基础。迁移与运行时仓储由 P2 实现，Host 尚不创建数据库。</summary>
public sealed class PlcMcpDbContext(DbContextOptions<PlcMcpDbContext> options) : DbContext(options)
{
    public DbSet<ProjectWorkspace> Workspaces => Set<ProjectWorkspace>();
    public DbSet<AutomationJob> Jobs => Set<AutomationJob>();
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var workspace = modelBuilder.Entity<ProjectWorkspace>();
        workspace.HasKey(x => x.Id);
        workspace.Property(x => x.Version).IsConcurrencyToken();
        workspace.Property(x => x.Revision).IsRequired();
        workspace.Property(x => x.ProjectPath).IsRequired();
        var job = modelBuilder.Entity<AutomationJob>();
        job.HasKey(x => x.Id);
        job.Property(x => x.Version).IsConcurrencyToken();
        job.Property(x => x.Status).HasConversion<string>();
        job.HasIndex(x => new { x.WorkspaceId, x.Operation, x.IdempotencyKey }).IsUnique();
        job.HasOne<ProjectWorkspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Restrict);
    }
}
