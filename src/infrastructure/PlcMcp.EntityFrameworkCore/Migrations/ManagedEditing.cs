using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
namespace PlcMcp.EntityFrameworkCore.Migrations;
[DbContext(typeof(PlcMcpDbContext))]
[Migration("20261106000200_ManagedEditing")]
public sealed class ManagedEditing : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TABLE "ManagedProjects" ("WorkspacePath" TEXT NOT NULL CONSTRAINT "PK_ManagedProjects" PRIMARY KEY, "Revision" TEXT NOT NULL, "HeadJson" TEXT NOT NULL);
        CREATE TABLE "ProjectChanges" ("JobId" TEXT NOT NULL CONSTRAINT "PK_ProjectChanges" PRIMARY KEY, "WorkspacePath" TEXT NOT NULL, "Stage" TEXT NOT NULL, "EvidenceJson" TEXT NOT NULL,
            CONSTRAINT "FK_ProjectChanges_Jobs_JobId" FOREIGN KEY ("JobId") REFERENCES "Jobs" ("Id") ON DELETE CASCADE);
        """);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("DROP TABLE \"ProjectChanges\"; DROP TABLE \"ManagedProjects\";");
}
