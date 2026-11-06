namespace PlcMcp.Core.Workspaces;

public static class ProjectChangeLifecycle
{
    public static bool CanAdvance(string current, string next) => current switch
    {
        "" => next == "preparing",
        "preparing" => next is "prepared" or "failed" or "cancelled" or "unknown",
        "prepared" => next is "executing" or "failed" or "cancelled" or "unknown",
        "executing" => next is "verified" or "failed" or "cancelled" or "unknown",
        "verified" => next is "published" or "failed" or "cancelled" or "unknown",
        _ => false
    };
}
