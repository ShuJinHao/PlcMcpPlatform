using System.Text.Json;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Workers;

namespace PlcMcp.Execution;

public sealed class BatchJournalStore(string directory) : IBatchJournal
{
    private string PathFor(Guid id)
    {
        var root = Path.GetFullPath(directory); Directory.CreateDirectory(root);
        var path = Path.Combine(root, id.ToString("N") + ".json");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 || File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ProjectEditException("batch_journal_path_invalid");
        return path;
    }
    public async Task WriteAsync(BatchJournal journal, CancellationToken token)
    {
        var path = PathFor(journal.JobId); var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            { await JsonSerializer.SerializeAsync(file, journal, WorkerJson.Options, token); await file.FlushAsync(token); file.Flush(true); }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public async Task<BatchJournal?> ReadAsync(Guid jobId, CancellationToken token)
    {
        var path = PathFor(jobId);
        if (!File.Exists(path)) return null;
        if (new FileInfo(path).Length > 4 * WorkerJson.MaxResponseBytes) throw new ProjectEditException("batch_journal_too_large");
        return JsonSerializer.Deserialize<BatchJournal>(await File.ReadAllTextAsync(path, token), WorkerJson.Options);
    }
}
