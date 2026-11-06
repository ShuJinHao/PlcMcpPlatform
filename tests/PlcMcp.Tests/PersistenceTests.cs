using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlcMcp.Core.Automation;
using PlcMcp.Core.Workspaces;
using PlcMcp.EntityFrameworkCore;
using Xunit;

namespace PlcMcp.Tests;

public sealed class PersistenceTests
{
    [Fact] public async Task Sqlite_enforces_idempotency_and_optimistic_concurrency()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<PlcMcpDbContext>().UseSqlite(connection).Options;
        await using var seed = new PlcMcpDbContext(options);
        await seed.Database.EnsureCreatedAsync(); // 测试专用，生产必须使用迁移。
        var workspace = ProjectWorkspace.Register("inovance", Path.GetFullPath("sample.project"), "r1");
        seed.Workspaces.Add(workspace);
        var job = AutomationJob.Queue(workspace.Id, "pou.update", "key", "hash", DateTimeOffset.UtcNow);
        seed.Jobs.Add(job); await seed.SaveChangesAsync();
        await using var first = new PlcMcpDbContext(options);
        await using var stale = new PlcMcpDbContext(options);
        var a = await first.Jobs.SingleAsync(); var b = await stale.Jobs.SingleAsync();
        a.Start(DateTimeOffset.UtcNow); await first.SaveChangesAsync();
        b.RequestCancellation(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var duplicate = new PlcMcpDbContext(options);
        duplicate.Jobs.Add(AutomationJob.Queue(workspace.Id, "pou.update", "key", "different-hash", DateTimeOffset.UtcNow));
        await Assert.ThrowsAsync<DbUpdateException>(() => duplicate.SaveChangesAsync());
    }
}
