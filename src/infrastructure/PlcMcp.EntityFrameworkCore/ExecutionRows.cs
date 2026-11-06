namespace PlcMcp.EntityFrameworkCore;

public sealed class JobRecord
{
    public Guid JobId { get; set; }
    public Guid RequestId { get; set; }
    public string InputJson { get; set; } = "";
    public string ExpectedRevision { get; set; } = "";
    public string ExecutionMode { get; set; } = "";
    public int TimeoutSeconds { get; set; }
    public string? RequestJson { get; set; }
    public string? ResponseJson { get; set; }
}
public sealed class ExecutionState
{
    public int Id { get; set; } = 1;
    public string ExecutionMode { get; set; } = "";
    public bool Blocked { get; set; }
    public string? Reason { get; set; }
}
