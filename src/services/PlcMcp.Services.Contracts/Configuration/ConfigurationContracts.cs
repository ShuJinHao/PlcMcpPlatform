using PlcMcp.Services.Contracts.Editing;

namespace PlcMcp.Services.Contracts.Configuration;

public static class ConfigurationOperations
{
    public const string TaskList = "task.list", TaskRead = "task.read", TaskConfigurationCreate = "task.configuration.create",
        TaskCreate = "task.create", TaskUpdate = "task.update", TaskDelete = "task.delete", TaskCallsSet = "task.calls.set",
        MemberRead = "member.read", MemberCreate = "member.create", MemberUpdate = "member.update",
        ObjectInspect = "object.inspect", FolderCreate = "folder.create", ObjectRename = "object.rename", ObjectMove = "object.move", ObjectDelete = "object.delete",
        LibraryList = "library.list", LibraryInstalled = "library.installed", LibraryAdd = "library.add", LibraryRemove = "library.remove", PlaceholderSet = "library.placeholder.set",
        DeviceTree = "device.tree.read", DeviceAdd = "device.add", DeviceRemove = "device.remove", ParameterRead = "device.parameters.read",
        ParameterWrite = "device.parameter.write", IoRead = "device.io.read", IoMap = "device.io.map";
    public static readonly string[] Reads = [TaskList, TaskRead, MemberRead, ObjectInspect, LibraryList, LibraryInstalled, DeviceTree, ParameterRead, IoRead];
    public static readonly string[] Writes = [TaskConfigurationCreate, TaskCreate, TaskUpdate, TaskDelete, TaskCallsSet, MemberCreate, MemberUpdate,
        FolderCreate, ObjectRename, ObjectMove, ObjectDelete, LibraryAdd, LibraryRemove, PlaceholderSet, DeviceAdd, DeviceRemove, ParameterWrite, IoMap];
    public static bool IsRead(string operation) => Reads.Contains(operation, StringComparer.Ordinal);
    public static bool IsWrite(string operation) => Writes.Contains(operation, StringComparer.Ordinal);
    public static bool IsCreate(string operation) => operation is TaskConfigurationCreate or TaskCreate or MemberCreate or FolderCreate or DeviceAdd;
    public static bool IsDelete(string operation) => operation is TaskDelete or ObjectDelete or DeviceRemove;
}

public sealed record TaskSettings(string Kind, string Priority, string? Interval = null, string? IntervalUnit = null,
    string? Event = null, string? ExternalEvent = null, string? ParentSynchronTask = null, int? CoreBinding = null, WatchdogSettings? Watchdog = null);
public sealed record WatchdogSettings(bool Enabled, string? Time = null, string? TimeUnit = null, string? Sensitivity = null);
public sealed record TaskCall(string PouId, string PouName, string Comment = "");
public sealed record AccessorSource(string? Declaration = null, string? Implementation = null);
public sealed record MemberSettings(string Kind, string? Language = null, string? ReturnType = null, AccessorSource? Getter = null, AccessorSource? Setter = null, string? ExpectedTypeId = null);
public sealed record ObjectSettings(string? ExpectedName = null, bool Recursive = false, int Index = -1);
public sealed record LibrarySettings(string Name, string? DefaultResolution = null, string? Redirection = null);
public sealed record DeviceSettings(DeviceRequirement Identification, string? Module = null, bool Plug = false);
public sealed record ParameterAddress(int ParameterId, int? ConnectorId = null, string[]? Elements = null);
public sealed record ParameterSettings(ParameterAddress Address, string Value);
public sealed record MappingSettings(ParameterAddress Address, string Variable, bool CreateVariable = false);
public sealed record ConfigurationChange(TaskSettings? Task = null, TaskCall[]? Calls = null, MemberSettings? Member = null,
    ObjectSettings? Object = null, LibrarySettings? Library = null, DeviceSettings? Device = null,
    ParameterSettings? Parameter = null, MappingSettings? Mapping = null);
public sealed record ConfigurationQuery(string? MemberKind = null, string? ExpectedTypeId = null);

// Project references observed in the saved project; installed packages are separate runtime facts.
public sealed record DeviceReference(string ObjectId, DeviceRequirement Identification, string Module);
public sealed record LibraryReference(string ManagerId, string Name, string Namespace, bool IsPlaceholder,
    string? DefaultResolution, string? EffectiveResolution, string? Redirection);
public sealed record DependencySnapshot(DeviceReference[] Devices, LibraryReference[] Libraries);
