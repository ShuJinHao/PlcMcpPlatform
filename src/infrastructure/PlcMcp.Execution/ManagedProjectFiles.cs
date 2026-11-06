using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

/// <summary>Bounded copies and immutable version directories. SQLite owns the current-version pointer.</summary>
public sealed class ManagedProjectFiles(string workspaceRoot) : IManagedProjectFiles
{
    private readonly string root = Canonical(workspaceRoot);
    private const long MaxBytes = 256L * 1024 * 1024;
    public static bool ValidName(string value) => Regex.IsMatch(value, "^[a-z][a-z0-9-]{0,47}$", RegexOptions.CultureInvariant)
        && !new[] { "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9", "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9" }.Contains(value);
    public string WorkspacePath(string name)
    {
        if (!ValidName(name)) throw new ProjectEditException("invalid_workspace_name");
        return Canonical(Path.Combine(root, "managed", name));
    }
    public async Task<TemplateView[]> ListTemplatesAsync(string mode, CancellationToken token)
    {
        var catalog = Path.Combine(root, "templates");
        GuardFuture(catalog);
        if (!Directory.Exists(catalog)) return [];
        var views = new List<TemplateView>();
        foreach (var directory in Directory.EnumerateDirectories(catalog).Take(257))
        {
            if (views.Count >= 256) throw new ProjectEditException("template_catalog_too_large");
            var id = Path.GetFileName(directory);
            try { var snapshot = await ReadTemplateAsync(id, mode, token); views.Add(new(id, snapshot.Revision, snapshot.Template, null)); }
            catch (ProjectEditException error) { views.Add(new(id, null, null, error.Code)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { views.Add(new(id, null, null, "invalid_template")); }
        }
        return views.OrderBy(x => x.Id, StringComparer.Ordinal).ToArray();
    }
    public async Task<ProjectSnapshot> ReadTemplateAsync(string templateId, string mode, CancellationToken token)
    {
        if (!ValidName(templateId)) throw new ProjectEditException("invalid_template_id");
        var directory = Canonical(Path.Combine(root, "templates", templateId));
        var descriptor = Within(directory, "template.json");
        GuardExisting(descriptor);
        if (new FileInfo(descriptor).Length > 65536) throw new ProjectEditException("template_too_large");
        var template = JsonSerializer.Deserialize<TemplateDefinition>(await File.ReadAllTextAsync(descriptor, token), WorkerJson.Options)
            ?? throw new ProjectEditException("invalid_template");
        if (template.Id != templateId || template.ExecutionMode != mode || template.Files is not { Length: > 0 and <= 512 }
            || template.RequiredLibraries is null || template.RequiredLibraries.Length > 128 || template.RequiredLibraries.Any(string.IsNullOrWhiteSpace)
            || mode == "real" && (template.Controller is null || template.Controller.Type < 0 || string.IsNullOrWhiteSpace(template.Controller.Id) || string.IsNullOrWhiteSpace(template.Controller.Version))
            || string.IsNullOrWhiteSpace(template.IdeProfile) || string.IsNullOrWhiteSpace(template.TargetController)
            || !template.Files.Contains(template.ProjectFile, StringComparer.Ordinal)
            || template.Files.Distinct(StringComparer.OrdinalIgnoreCase).Count() != template.Files.Length
            || template.Files.Contains("template.json", StringComparer.OrdinalIgnoreCase)) throw new ProjectEditException("invalid_template");
        var stamps = await CaptureAsync(directory, template.Files, token);
        var fingerprint = Hash(JsonSerializer.SerializeToUtf8Bytes(new { template, files = stamps }, WorkerJson.Options));
        return new(directory, template.ProjectFile, "t2:" + fingerprint, stamps, template);
    }
    public async Task VerifyAsync(ProjectSnapshot snapshot, CancellationToken token)
    {
        if (snapshot.Revision.StartsWith("t2:", StringComparison.Ordinal))
        {
            var actual = await ReadTemplateAsync(snapshot.Template.Id, snapshot.Template.ExecutionMode, token);
            if (actual.Revision != snapshot.Revision) throw new ProjectEditException("workspace_revision_conflict");
        }
        else
        {
            var actual = await CaptureAsync(snapshot.Directory, Enumerate(snapshot.Directory), token);
            if (!actual.SequenceEqual(snapshot.Files)) throw new ProjectEditException("workspace_revision_conflict");
        }
    }
    public async Task<(ProjectSnapshot Candidate, string BackupPath)> PrepareAsync(string workspacePath, Guid jobId, ProjectSnapshot source, CancellationToken token)
    {
        var change = Path.Combine(workspacePath, "changes", jobId.ToString("N"));
        GuardFuture(change);
        if (Directory.Exists(change)) throw new ProjectEditException("change_already_exists");
        await VerifyAsync(source, token);
        var backup = Path.Combine(change, "before"); var candidate = Path.Combine(change, "candidate");
        Directory.CreateDirectory(backup); Directory.CreateDirectory(candidate);
        foreach (var stamp in source.Files)
        {
            await CopyAsync(Within(source.Directory, stamp.Path), Within(backup, stamp.Path), token);
            await CopyAsync(Within(backup, stamp.Path), Within(candidate, stamp.Path), token);
        }
        await VerifyAsync(source, token);
        var copied = await CaptureAsync(candidate, Enumerate(candidate), token);
        var backedUp = await CaptureAsync(backup, Enumerate(backup), token);
        if (!copied.SequenceEqual(source.Files) || !backedUp.SequenceEqual(source.Files)) throw new ProjectEditException("backup_verification_failed");
        return (new(candidate, source.ProjectFile, "candidate", copied, source.Template, source.Dependencies), backup);
    }
    public async Task<ProjectSnapshot> SealAsync(string workspacePath, Guid jobId, ProjectSnapshot candidate, CancellationToken token)
    {
        var stamps = await CaptureAsync(candidate.Directory, Enumerate(candidate.Directory), token);
        if (!stamps.Any(x => x.Path == candidate.ProjectFile)) throw new ProjectEditException("project_unavailable");
        var versionPath = Path.Combine(workspacePath, "versions", jobId.ToString("N"));
        GuardFuture(versionPath); Directory.CreateDirectory(Path.GetDirectoryName(versionPath)!);
        token.ThrowIfCancellationRequested();
        Directory.Move(candidate.Directory, versionPath);
        return new(versionPath, candidate.ProjectFile, "m2:" + jobId.ToString("N") + ":" + Hash(JsonSerializer.SerializeToUtf8Bytes(stamps, WorkerJson.Options)), stamps, candidate.Template, candidate.Dependencies);
    }
    public async Task<string> ProjectFileRevisionAsync(ProjectSnapshot snapshot, CancellationToken token)
    {
        var path = Within(snapshot.Directory, snapshot.ProjectFile); GuardExisting(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
    }
    private async Task<FileStamp[]> CaptureAsync(string directory, IEnumerable<string> paths, CancellationToken token)
    {
        var result = new List<FileStamp>(); long total = 0;
        foreach (var relative in paths.Order(StringComparer.Ordinal))
        {
            if (result.Count >= 2000) throw new ProjectEditException("project_too_large");
            var path = Within(directory, relative); GuardExisting(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            var length = stream.Length; total += length;
            if (total > MaxBytes) throw new ProjectEditException("project_too_large");
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).ToLowerInvariant();
            if (stream.Length != length) throw new ProjectEditException("workspace_revision_conflict");
            result.Add(new(relative, length, digest));
        }
        if (result.Select(x => x.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Count)
            throw new ProjectEditException("ambiguous_project_paths");
        return result.ToArray();
    }
    private IEnumerable<string> Enumerate(string directory)
    {
        GuardExisting(directory); var queue = new Queue<(string Path, int Depth)>(); queue.Enqueue((directory, 0)); var count = 0;
        while (queue.TryDequeue(out var current))
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(current.Path))
            {
                if (++count > 4000 || current.Depth > 32) throw new ProjectEditException("project_too_large");
                GuardExisting(path);
                if (Directory.Exists(path)) queue.Enqueue((path, current.Depth + 1));
                else yield return Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/');
            }
        }
    }
    private static string Within(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Length > 1024 || relative.Contains('\\') || relative.Contains(':')
            || relative.Split('/').Any(x => x is "" or "." or ".." || x.EndsWith('.') || x.EndsWith(' ')) || Path.IsPathRooted(relative))
            throw new ProjectEditException("path_not_allowed");
        return Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
    }
    private void GuardExisting(string path) { GuardFuture(path); if (!File.Exists(path) && !Directory.Exists(path)) throw new ProjectEditException("project_unavailable"); }
    private void GuardFuture(string path)
    {
        var full = Canonical(path);
        if (full != root && !full.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ProjectEditException("path_not_allowed");
        var current = root;
        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("path_not_allowed");
        foreach (var part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            if (File.Exists(current) || Directory.Exists(current))
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ProjectEditException("path_not_allowed");
        }
    }
    private async Task CopyAsync(string source, string target, CancellationToken token)
    {
        GuardExisting(source); GuardFuture(target); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        await using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true);
        await input.CopyToAsync(output, token); await output.FlushAsync(token); output.Flush(true);
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static string Canonical(string path) => OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path);
}
