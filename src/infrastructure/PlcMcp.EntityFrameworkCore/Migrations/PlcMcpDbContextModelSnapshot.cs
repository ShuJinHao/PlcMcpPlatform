using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using PlcMcp.Core.Automation;

namespace PlcMcp.EntityFrameworkCore.Migrations;

[DbContext(typeof(PlcMcpDbContext))]
public sealed class PlcMcpDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "8.0.33");
        modelBuilder.Entity("PlcMcp.Core.Workspaces.ProjectWorkspace", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            b.Property<string>("VendorId").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ProjectPath").IsRequired().HasColumnType("TEXT");
            b.Property<string>("Revision").IsRequired().HasColumnType("TEXT");
            b.Property<long>("Version").IsConcurrencyToken().HasColumnType("INTEGER");
            b.HasKey("Id"); b.HasIndex("ProjectPath").IsUnique(); b.ToTable("Workspaces");
        });
        modelBuilder.Entity("PlcMcp.Core.Automation.AutomationJob", b =>
        {
            b.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            b.Property<Guid>("WorkspaceId").HasColumnType("TEXT");
            b.Property<string>("Operation").IsRequired().HasColumnType("TEXT");
            b.Property<string>("IdempotencyKey").IsRequired().HasColumnType("TEXT");
            b.Property<string>("RequestFingerprint").IsRequired().HasColumnType("TEXT");
            b.Property<JobStatus>("Status").HasConversion<string>().HasColumnType("TEXT");
            b.Property<string>("OutcomeCode").HasColumnType("TEXT");
            b.Property<long>("Version").IsConcurrencyToken().HasColumnType("INTEGER");
            b.Property<DateTimeOffset>("CreatedAt").HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)).HasColumnType("INTEGER");
            b.Property<DateTimeOffset>("UpdatedAt").HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v)).HasColumnType("INTEGER");
            b.HasKey("Id"); b.HasIndex("WorkspaceId", "Operation", "IdempotencyKey").IsUnique(); b.ToTable("Jobs");
        });
        modelBuilder.Entity("PlcMcp.EntityFrameworkCore.JobRecord", b =>
        {
            b.Property<Guid>("JobId").HasColumnType("TEXT"); b.Property<Guid>("RequestId").HasColumnType("TEXT");
            b.Property<string>("InputJson").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ExpectedRevision").IsRequired().HasColumnType("TEXT");
            b.Property<string>("ExecutionMode").IsRequired().HasColumnType("TEXT");
            b.Property<int>("TimeoutSeconds").HasColumnType("INTEGER");
            b.Property<string>("RequestJson").HasColumnType("TEXT"); b.Property<string>("ResponseJson").HasColumnType("TEXT");
            b.HasKey("JobId"); b.ToTable("JobRecords");
        });
        modelBuilder.Entity("PlcMcp.EntityFrameworkCore.ExecutionState", b =>
        {
            b.Property<int>("Id").ValueGeneratedOnAdd().HasColumnType("INTEGER");
            b.Property<string>("ExecutionMode").IsRequired().HasColumnType("TEXT");
            b.Property<bool>("Blocked").HasColumnType("INTEGER"); b.Property<string>("Reason").HasColumnType("TEXT");
            b.HasKey("Id"); b.ToTable("ExecutionStates");
        });
        modelBuilder.Entity("PlcMcp.Core.Automation.AutomationJob", b => b.HasOne("PlcMcp.Core.Workspaces.ProjectWorkspace", null)
            .WithMany().HasForeignKey("WorkspaceId").OnDelete(DeleteBehavior.Restrict).IsRequired());
        modelBuilder.Entity("PlcMcp.EntityFrameworkCore.JobRecord", b => b.HasOne("PlcMcp.Core.Automation.AutomationJob", null)
            .WithOne().HasForeignKey("PlcMcp.EntityFrameworkCore.JobRecord", "JobId").OnDelete(DeleteBehavior.Cascade).IsRequired());
    }
}
