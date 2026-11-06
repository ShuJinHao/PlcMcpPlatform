using System.Security.Cryptography;
using System.Text.Json;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Maintenance;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

/// <summary>Offline maintenance under the same InstanceOwnership lock as the host.
/// Published versions, backups, exchange artifacts, job records and vendor evidence are never candidates.</summary>
public sealed class RetentionMaintenance(string dataDirectory, string workspaceRoot, IRetentionStateSource states)
{
    private readonly string data = Path.GetFullPath(dataDirectory);
    private readonly string workspace = Path.GetFullPath(workspaceRoot);
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, WorkerJson.Options))).ToLowerInvariant();
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    private static void Guard(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar).Contains("..")) throw new ProjectEditException("maintenance_path_not_allowed");
        var current = root;
        if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("maintenance_link_rejected");
        foreach (var part in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("maintenance_link_rejected");
        }
    }
    private static async Task<T> Read<T>(string path, int maximum, CancellationToken token)
    {
        if (new FileInfo(path).Length > maximum) throw new ProjectEditException("maintenance_metadata_too_large");
        return JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(path, token), WorkerJson.Options) ?? throw new ProjectEditException("maintenance_invalid_metadata");
    }
    private static async Task<RetentionFile> Stamp(string root, string path, CancellationToken token)
    {
        Guard(root, path);
        var info = new FileInfo(path); var ticks = info.LastWriteTimeUtc.Ticks;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 272L * 1024 * 1024) throw new ProjectEditException("maintenance_file_too_large");
        return new(Path.GetRelativePath(root, path), stream.Length, ticks, Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant());
    }
    private async Task<RetentionPlan> Scan(RetentionPolicy policy, DateTimeOffset now, CancellationToken token)
    {
        if (policy.UploadDays is < 1 or > 36500 || policy.FailedCandidateDays is < 1 or > 36500 || policy.DeliveryDays is < 1 or > 36500
            || (policy.PinnedDeliveryIds ?? []).Any(id => !Digest(id))) throw new ProjectEditException("maintenance_invalid_policy");
        Guard(data, data); Guard(workspace, workspace);
        var state = await states.ReadAsync(token);
        if (state.Blocked) throw new ProjectEditException("maintenance_execution_unresolved");
        var items = new List<RetentionItem>();
        var uploads = Path.Combine(data, "uploads"); Guard(data, uploads);
        if (Directory.Exists(uploads))
        foreach (var meta in Directory.EnumerateFiles(uploads, "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(meta); if (!Digest(id)) continue;
            Guard(data, meta); var upload = await Read<UploadSession>(meta, 4096, token);
            if (upload.UploadId != id || !Digest(upload.Sha256) || !ExchangeOperations.Format(upload.Format)) throw new ProjectEditException("maintenance_invalid_upload");
            var paths = new[] { meta, Path.Combine(uploads, id + ".part") }.Where(File.Exists).ToArray();
            foreach (var path in paths) Guard(data, path);
            if (paths.Any(p => File.GetLastWriteTimeUtc(p) >= now.AddDays(-policy.UploadDays).UtcDateTime)) continue;
            var files = new List<RetentionFile>(); foreach (var path in paths) files.Add(await Stamp(data, path, token));
            items.Add(new("upload", id, "data", Path.GetRelativePath(data, meta), files.ToArray(), []));
        }
        foreach (var failed in state.Failures)
        {
            var e = failed.Evidence;
            if (!e.ExecutorStopped || e.Stage is not ("failed" or "cancelled") || e.CandidatePath is null || failed.FinishedAt >= now.AddDays(-policy.FailedCandidateDays)) continue;
            var name = Path.GetFileName(e.WorkspacePath);
            if (!ManagedProjectFiles.ValidName(name)) throw new ProjectEditException("maintenance_invalid_workspace");
            var target = Path.Combine(workspace, "managed", name, "changes", e.JobId.ToString("N"), "candidate");
            if (!Path.GetFullPath(e.WorkspacePath).Equals(Path.Combine(workspace, "managed", name), PathComparison)
                || !Path.GetFullPath(e.CandidatePath).Equals(target, PathComparison)) throw new ProjectEditException("maintenance_path_not_allowed");
            Guard(workspace, target);
            if (!Directory.Exists(target)) continue;
            if (state.Heads.Any(h => Path.GetFullPath(h.Snapshot.Directory).Equals(target, PathComparison))) continue;
            var paths = new List<string>(); var directories = new List<string>(); var queue = new Queue<string>(); queue.Enqueue(target);
            while (queue.TryDequeue(out var directory))
            {
                Guard(workspace, directory); directories.Add(Path.GetRelativePath(workspace, directory));
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    Guard(workspace, path);
                    if (paths.Count + directories.Count + queue.Count > 5000) throw new ProjectEditException("maintenance_candidate_too_large");
                    if (Directory.Exists(path)) queue.Enqueue(path); else paths.Add(path);
                }
            }
            if (paths.Any(p => File.GetLastWriteTimeUtc(p) >= now.AddDays(-policy.FailedCandidateDays).UtcDateTime)) continue;
            var files = new List<RetentionFile>(); foreach (var path in paths.Order(StringComparer.Ordinal)) files.Add(await Stamp(workspace, path, token));
            items.Add(new("candidate", e.JobId.ToString("N"), "workspace", Path.GetRelativePath(workspace, target), files.ToArray(), directories.Order(StringComparer.Ordinal).ToArray()));
        }
        var deliveries = Path.Combine(data, "deliveries"); Guard(data, deliveries);
        if (Directory.Exists(deliveries))
        foreach (var meta in Directory.EnumerateFiles(deliveries, "*.json").Order(StringComparer.Ordinal))
        {
            var id = Path.GetFileNameWithoutExtension(meta); if (!Digest(id) || (policy.PinnedDeliveryIds ?? []).Contains(id)) continue;
            Guard(data, meta); var artifact = await Read<DeliveryArtifact>(meta, 4096, token);
            if (artifact.ArtifactId != id || artifact.Sha256 != id) throw new ProjectEditException("maintenance_invalid_delivery");
            if (state.Heads.Any(h => h.WorkspaceName == artifact.WorkspaceName && h.Snapshot.Revision == artifact.Revision)) continue;
            var zip = Path.Combine(deliveries, id + ".zip"); Guard(data, zip);
            if (!File.Exists(zip) || new[] { meta, zip }.Any(p => File.GetLastWriteTimeUtc(p) >= now.AddDays(-policy.DeliveryDays).UtcDateTime)) continue;
            var stamp = await Stamp(data, zip, token);
            if (stamp.Sha256 != id || stamp.Bytes != artifact.ByteLength) throw new ProjectEditException("maintenance_delivery_integrity_mismatch");
            items.Add(new("delivery", id, "data", Path.GetRelativePath(data, zip), [await Stamp(data, meta, token), stamp], []));
        }
        if (items.Count > 10000) throw new ProjectEditException("maintenance_plan_too_large");
        var plan = new RetentionPlan(1, data, workspace, now, policy, state.Fingerprint, items.OrderBy(i => i.Kind + i.Id, StringComparer.Ordinal).ToArray(), "");
        if (JsonSerializer.SerializeToUtf8Bytes(plan, WorkerJson.Options).Length > 4 * 1024 * 1024 - 128) throw new ProjectEditException("maintenance_plan_too_large");
        return plan with { PlanSha256 = Hash(plan) };
    }
    public async Task<RetentionPlan> PreviewAsync(RetentionPolicy policy, DateTimeOffset now, CancellationToken token)
    {
        using var owner = new InstanceOwnership(data);
        return await Scan(policy, now, token);
    }
    public async Task<RetentionReceipt> ApplyAsync(RetentionPlan plan, DateTimeOffset now, CancellationToken token)
    {
        using var owner = new InstanceOwnership(data);
        if (plan.SchemaVersion != 1 || plan.Policy is null || plan.Items is null || !Digest(plan.PlanSha256) || plan.PlanSha256 != Hash(plan with { PlanSha256 = "" })
            || plan.DataDirectory != data || plan.WorkspaceRoot != workspace) throw new ProjectEditException("maintenance_plan_invalid");
        var audit = Path.Combine(data, "maintenance"); Guard(data, audit); Directory.CreateDirectory(audit);
        var receiptPath = Path.Combine(audit, plan.PlanSha256 + ".json"); Guard(data, receiptPath);
        if (File.Exists(receiptPath))
        {
            var prior = await Read<RetentionReceipt>(receiptPath, 4 * 1024 * 1024, token);
            if (prior.PlanSha256 != plan.PlanSha256) throw new ProjectEditException("maintenance_receipt_invalid");
            if (prior.State == "completed") return prior;
            throw new ProjectEditException("maintenance_partial_requires_new_preview");
        }
        if (plan.EvaluatedAt > now || now - plan.EvaluatedAt > TimeSpan.FromHours(24)) throw new ProjectEditException("maintenance_plan_expired");
        // Re-evaluate eligibility, every file digest and all directory membership before any deletion.
        var actual = await Scan(plan.Policy, plan.EvaluatedAt, token);
        if (actual.PlanSha256 != plan.PlanSha256) throw new ProjectEditException("maintenance_plan_stale");
        var completed = new List<string>();
        await Save(receiptPath, new RetentionReceipt(plan.PlanSha256, "applying", [], now), token);
        try
        {
            foreach (var item in actual.Items)
            {
                token.ThrowIfCancellationRequested(); var root = item.Root == "data" ? data : workspace;
                if (item.Kind is "upload" or "delivery")
                {
                    var marker = Path.Combine(data, item.Kind == "upload" ? "uploads" : "deliveries", item.Id + ".expired.json");
                    Guard(data, marker);
                    await Save(marker, new { artifactId = item.Id, expiredAt = now, planSha256 = plan.PlanSha256 }, token);
                }
                foreach (var file in item.Files)
                {
                    // Delivery metadata is kept so historical job results remain intelligible.
                    if (item.Kind == "delivery" && file.Path.EndsWith(".json", StringComparison.Ordinal)) continue;
                    var path = Path.Combine(root, file.Path); Guard(root, path); File.Delete(path);
                }
                // Never recursively delete: a concurrently added file stops removal, rather than being erased.
                foreach (var directory in item.Directories.OrderByDescending(p => p.Length))
                { var path = Path.Combine(root, directory); Guard(root, path); Directory.Delete(path, false); }
                completed.Add(item.Kind + ":" + item.Id);
                await Save(receiptPath, new RetentionReceipt(plan.PlanSha256, "applying", completed.ToArray(), now), CancellationToken.None);
            }
            var result = new RetentionReceipt(plan.PlanSha256, "completed", completed.ToArray(), now);
            await Save(receiptPath, result, CancellationToken.None); return result;
        }
        catch
        {
            await Save(receiptPath, new RetentionReceipt(plan.PlanSha256, "partial", completed.ToArray(), now, "maintenance_interrupted"), CancellationToken.None);
            throw;
        }
    }
    private static async Task Save<T>(string path, T value, CancellationToken token)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(stream, value, WorkerJson.Options, token); await stream.FlushAsync(token); stream.Flush(true); }
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
