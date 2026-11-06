using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace PlcMcp.EntityFrameworkCore.Migrations;

[DbContext(typeof(PlcMcpDbContext))]
[Migration("202611060001_InitialExecution")]
public sealed class InitialExecution : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE Workspaces (Id TEXT NOT NULL PRIMARY KEY, VendorId TEXT NOT NULL,
              ProjectPath TEXT NOT NULL, Revision TEXT NOT NULL, Version INTEGER NOT NULL);
            CREATE UNIQUE INDEX IX_Workspaces_ProjectPath ON Workspaces(ProjectPath);
            CREATE TABLE Jobs (Id TEXT NOT NULL PRIMARY KEY, WorkspaceId TEXT NOT NULL,
              Operation TEXT NOT NULL, IdempotencyKey TEXT NOT NULL, RequestFingerprint TEXT NOT NULL,
              Status TEXT NOT NULL, OutcomeCode TEXT NULL, Version INTEGER NOT NULL,
              CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL,
              FOREIGN KEY (WorkspaceId) REFERENCES Workspaces(Id) ON DELETE RESTRICT);
            CREATE UNIQUE INDEX IX_Jobs_WorkspaceId_Operation_IdempotencyKey ON Jobs(WorkspaceId,Operation,IdempotencyKey);
            CREATE TABLE JobRecords (JobId TEXT NOT NULL PRIMARY KEY, RequestId TEXT NOT NULL,
              InputJson TEXT NOT NULL, ExpectedRevision TEXT NOT NULL, ExecutionMode TEXT NOT NULL,
              TimeoutSeconds INTEGER NOT NULL, RequestJson TEXT NULL, ResponseJson TEXT NULL,
              FOREIGN KEY (JobId) REFERENCES Jobs(Id) ON DELETE CASCADE);
            CREATE TABLE ExecutionStates (Id INTEGER NOT NULL PRIMARY KEY, ExecutionMode TEXT NOT NULL,
              Blocked INTEGER NOT NULL, Reason TEXT NULL);
            """);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DROP TABLE JobRecords; DROP TABLE Jobs; DROP TABLE Workspaces; DROP TABLE ExecutionStates;");
}
