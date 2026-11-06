using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PlcMcp.EntityFrameworkCore;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Maintenance;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.McpHost;

public static class MaintenanceCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            if (args.Length < 2 || args[0] != "--maintenance" || args[1] is not ("preview" or "apply") || args.Length % 2 != 0)
                throw new ProjectEditException("maintenance_arguments_invalid");
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 2; i < args.Length; i += 2)
                if (!new[] { "--data-directory", "--workspace-root", "--policy", "--report", "--plan" }.Contains(args[i]) || !fields.TryAdd(args[i], args[i + 1]))
                    throw new ProjectEditException("maintenance_arguments_invalid");
            string Required(string key) => Path.GetFullPath(fields.TryGetValue(key, out var value) ? value : throw new ProjectEditException("maintenance_arguments_invalid"));
            var data = Required("--data-directory"); var workspace = Required("--workspace-root");
            var database = Path.Combine(data, "execution.db");
            if (!File.Exists(database) || !Directory.Exists(workspace) || (File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
                throw new ProjectEditException("maintenance_database_required");
            var options = new DbContextOptionsBuilder<PlcMcpDbContext>().UseSqlite(new SqliteConnectionStringBuilder
                { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()).Options;
            var maintenance = new RetentionMaintenance(data, workspace, new SqliteRetentionState(options));
            async Task<T> Read<T>(string path)
            {
                if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new ProjectEditException("maintenance_metadata_too_large");
                var strict = new JsonSerializerOptions(WorkerJson.Options) { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };
                return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path), strict) ?? throw new ProjectEditException("maintenance_invalid_metadata");
            }
            if (args[1] == "preview")
            {
                if (fields.ContainsKey("--plan")) throw new ProjectEditException("maintenance_arguments_invalid");
                var report = Required("--report");
                foreach (var root in new[] { data, workspace })
                {
                    var relative = Path.GetRelativePath(root, report);
                    if (!Path.IsPathRooted(relative) && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ProjectEditException("maintenance_report_outside_storage_required");
                }
                var policy = fields.ContainsKey("--policy") ? await Read<RetentionPolicy>(Required("--policy")) : new RetentionPolicy();
                var plan = await maintenance.PreviewAsync(policy, DateTimeOffset.UtcNow, default);
                await using var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await JsonSerializer.SerializeAsync(output, plan, WorkerJson.Options);
                Console.WriteLine(JsonSerializer.Serialize(new { plan.PlanSha256, items = plan.Items.Length,
                    bytesToDelete = plan.Items.Sum((RetentionItem item) => item.Files.Where(file => item.Kind != "delivery" || file.Path.EndsWith(".zip", StringComparison.Ordinal)).Sum(file => file.Bytes)), report }, WorkerJson.Options));
            }
            else
            {
                if (fields.ContainsKey("--policy") || fields.ContainsKey("--report")) throw new ProjectEditException("maintenance_arguments_invalid");
                var plan = await Read<RetentionPlan>(Required("--plan"));
                var receipt = await maintenance.ApplyAsync(plan, DateTimeOffset.UtcNow, default);
                Console.WriteLine(JsonSerializer.Serialize(receipt, WorkerJson.Options));
            }
            return 0;
        }
        catch (Exception error) when (error is ProjectEditException or IOException or UnauthorizedAccessException or JsonException or SqliteException or InvalidOperationException or ArgumentException)
        {
            Console.Error.WriteLine(JsonSerializer.Serialize(new { errorCode = error is ProjectEditException known ? known.Code : error is IOException ? "maintenance_storage_busy_or_changed" : "maintenance_failed", deleted = "inspect_maintenance_receipt" }, WorkerJson.Options));
            return 2;
        }
    }
}
