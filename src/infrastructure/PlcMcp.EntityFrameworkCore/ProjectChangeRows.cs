namespace PlcMcp.EntityFrameworkCore;

public sealed class ManagedProjectRow
{
    public string WorkspacePath { get; set; } = "";
    public string Revision { get; set; } = "";
    public string HeadJson { get; set; } = "";
}
public sealed class ProjectChangeRow
{
    public Guid JobId { get; set; }
    public string WorkspacePath { get; set; } = "";
    public string Stage { get; set; } = "";
    public string EvidenceJson { get; set; } = "";
}
