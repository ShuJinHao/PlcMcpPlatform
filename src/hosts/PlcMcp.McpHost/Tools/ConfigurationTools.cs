using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using PlcMcp.EngineeringService.Editing;
using PlcMcp.EngineeringService.Execution;
using PlcMcp.Execution;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;
using C = PlcMcp.Services.Contracts.Configuration.ConfigurationOperations;

namespace PlcMcp.McpHost.Tools;

[McpServerToolType]
public sealed class ConfigurationTools(EngineeringJobs queries, ManagedEditingJobs edits, IEngineeringExecutor executor, ExecutionSignal signal)
{
    [McpServerTool(Name = "plc_task_list", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read task configurations and tasks under an explicit Application. Preserve call order and units.")]
    public Task<CallToolResult> Tasks(string projectPath, string applicationId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.TaskList, projectPath, applicationId, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_task_read", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read one task with its settings and ordered program calls.")]
    public Task<CallToolResult> Task(string projectPath, string taskId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.TaskRead, projectPath, taskId, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_task_configuration_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create the Task Configuration under an Application if absent; no PLC online action.")]
    public Task<CallToolResult> CreateTaskConfiguration(string workspaceName, string expectedRevision, string applicationId, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.TaskConfigurationCreate, new(workspaceName, ParentId: applicationId), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_task_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create a task under an existing Task Configuration, with explicit kind, units, priority and watchdog.")]
    public Task<CallToolResult> CreateTask(string workspaceName, string expectedRevision, string taskConfigurationId, string name, TaskSettings settings, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.TaskCreate, new(workspaceName, ParentId: taskConfigurationId, Name: name, Configuration: new(Task: settings)), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_task_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Replace task scheduling settings. Omitted watchdog/core binding remain unchanged; kind-specific fields must be valid.")]
    public Task<CallToolResult> UpdateTask(string workspaceName, string expectedRevision, string taskId, TaskSettings settings, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.TaskUpdate, new(workspaceName, ObjectId: taskId, Configuration: new(Task: settings)), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_task_delete", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Remove a task from a candidate version after checking its current name. Older versions remain available.")]
    public Task<CallToolResult> DeleteTask(string workspaceName, string expectedRevision, string taskId, string expectedName, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.TaskDelete, new(workspaceName, ObjectId: taskId, Configuration: new(Object: new(ExpectedName: expectedName))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_task_set_calls", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Replace the complete ordered task call list. Empty array unbinds all. Each call supplies the Program GUID, name and comment; rejects ambiguous names and non-Program POUs.")]
    public Task<CallToolResult> SetTaskCalls(string workspaceName, string expectedRevision, string taskId, TaskCall[] calls, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.TaskCallsSet, new(workspaceName, ObjectId: taskId, Configuration: new(Calls: calls)), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_pou_member_read", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read Method/Property/Action source by GUID, including Property Get/Set children. Kind selects the expected member shape.")]
    public Task<CallToolResult> Member(string projectPath, string objectId, string memberKind, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120, string? expectedTypeId = null)
        => Read(C.MemberRead, projectPath, objectId, new(MemberKind: memberKind, ExpectedTypeId: expectedTypeId), idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_pou_member_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create Method/Property/Action under an eligible parent. Property bodies are getter/setter sources. Null source leaves generated content unchanged; graph languages create empty content.")]
    public Task<CallToolResult> CreateMember(string workspaceName, string expectedRevision, string parentId, string name, MemberSettings settings, string? declaration, string? implementation, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.MemberCreate, new(workspaceName, ParentId: parentId, Name: name, Declaration: declaration, Implementation: implementation, Configuration: new(Member: settings)), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_pou_member_update", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Update member source by GUID and expected kind; save/reopen/readback the member and property accessors. Does not change its language or return type.")]
    public Task<CallToolResult> UpdateMember(string workspaceName, string expectedRevision, string objectId, MemberSettings settings, string? declaration, string? implementation, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.MemberUpdate, new(workspaceName, ObjectId: objectId, Declaration: declaration, Implementation: implementation, Configuration: new(Member: settings)), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_object_inspect", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Inspect a node, children and known task-call impacts. Use project for the root. This is not a compiler reference analysis.")]
    public Task<CallToolResult> Inspect(string projectPath, string objectId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.ObjectInspect, projectPath, objectId, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_folder_create", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create a folder under a known object or project root. Return its actual GUID after creation.")]
    public Task<CallToolResult> CreateFolder(string workspaceName, string expectedRevision, string parentId, string name, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.FolderCreate, new(workspaceName, ParentId: parentId, Name: name, Configuration: new(Object: new())), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_object_rename", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Rename a non-system object after checking its current name and task-call references. Does not promise global source refactoring.")]
    public Task<CallToolResult> Rename(string workspaceName, string expectedRevision, string objectId, string expectedName, string name, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.ObjectRename, new(workspaceName, ObjectId: objectId, Name: name, Configuration: new(Object: new(ExpectedName: expectedName))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_object_move", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Move an object to a permitted parent with optional child index (-1 appends). Reject cycles, name collisions and known call dependencies.")]
    public Task<CallToolResult> Move(string workspaceName, string expectedRevision, string objectId, string expectedName, string parentId, int index, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.ObjectMove, new(workspaceName, ObjectId: objectId, ParentId: parentId, Configuration: new(Object: new(ExpectedName: expectedName, Index: index))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_object_delete", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Delete an object in a candidate. Nonempty subtree requires recursive=true; protected roots and known task dependencies are rejected.")]
    public Task<CallToolResult> Delete(string workspaceName, string expectedRevision, string objectId, string expectedName, bool recursive, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.ObjectDelete, new(workspaceName, ObjectId: objectId, Configuration: new(Object: new(ExpectedName: expectedName, Recursive: recursive))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_library_list", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read references and placeholder resolution from an existing library manager. Does not create a manager during a read.")]
    public Task<CallToolResult> Libraries(string projectPath, string libraryManagerId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.LibraryList, projectPath, libraryManagerId, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_library_list_installed", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Query the IDE library repository. Exact display names include version/company. Does not install or uninstall libraries.")]
    public Task<CallToolResult> InstalledLibraries(string projectPath, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.LibraryInstalled, projectPath, null, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_library_add", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Add a reference to one exact installed library display name. Fail when missing or ambiguous.")]
    public Task<CallToolResult> AddLibrary(string workspaceName, string expectedRevision, string libraryManagerId, string displayName, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.LibraryAdd, new(workspaceName, ObjectId: libraryManagerId, Configuration: new(Library: new(displayName))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_library_remove", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Remove an explicit project library reference by exact name; system references are protected.")]
    public Task<CallToolResult> RemoveLibrary(string workspaceName, string expectedRevision, string libraryManagerId, string referenceName, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.LibraryRemove, new(workspaceName, ObjectId: libraryManagerId, Configuration: new(Library: new(referenceName))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_library_set_placeholder", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Create/update a placeholder with explicit default resolution and/or redirection. Null leaves a setting unchanged; empty redirection clears it. Return effective resolution separately.")]
    public Task<CallToolResult> SetPlaceholder(string workspaceName, string expectedRevision, string libraryManagerId, string name, string? defaultResolution, string? redirection, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.PlaceholderSet, new(workspaceName, ObjectId: libraryManagerId, Configuration: new(Library: new(name, defaultResolution, redirection))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_device_get_tree", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Read device identities, module IDs and parent relationships. No online connection.")]
    public Task<CallToolResult> Devices(string projectPath, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.DeviceTree, projectPath, null, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_device_add", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Add or plug a device under an explicit parent using exact type/id/version/module. Device packages must already exist in the IDE.")]
    public Task<CallToolResult> AddDevice(string workspaceName, string expectedRevision, string parentId, string name, DeviceSettings settings, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.DeviceAdd, new(workspaceName, ParentId: parentId, Name: name, Configuration: new(Device: settings)), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_device_remove", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Remove a device in a candidate; explicitly acknowledge nonempty subtrees. The template controller and application roots are protected.")]
    public Task<CallToolResult> RemoveDevice(string workspaceName, string expectedRevision, string deviceId, string expectedName, bool recursive, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.DeviceRemove, new(workspaceName, ObjectId: deviceId, Configuration: new(Object: new(ExpectedName: expectedName, Recursive: recursive))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_device_get_parameters", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Enumerate offline device/connector parameters and nested element addresses, types, access rights and values; truncation is explicit.")]
    public Task<CallToolResult> Parameters(string projectPath, string deviceId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.ParameterRead, projectPath, deviceId, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_device_set_parameter", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Write one primitive offline parameter by device GUID and structural address. Check declared type, range/enum and offline access; never call online write APIs.")]
    public Task<CallToolResult> SetParameter(string workspaceName, string expectedRevision, string deviceId, ParameterAddress address, string value, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.ParameterWrite, new(workspaceName, ObjectId: deviceId, Configuration: new(Parameter: new(address, value))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_device_get_io_mappings", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Enumerate mappable IO variables with stable connector/parameter/element addresses. Not EtherCAT PDO configuration.")]
    public Task<CallToolResult> IoMappings(string projectPath, string deviceId, string idempotencyKey, CancellationToken cancellationToken, string? expectedRevision = null, int maxObjects = 256, int timeoutSeconds = 120)
        => Read(C.IoRead, projectPath, deviceId, null, idempotencyKey, expectedRevision, maxObjects, timeoutSeconds, cancellationToken);

    [McpServerTool(Name = "plc_device_set_io_mapping", ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false, UseStructuredContent = true, OutputSchemaType = typeof(JobToolReply))]
    [Description("Map one channel to a qualified existing variable, explicitly create an unqualified variable, or clear with an empty expression. Read back creation/existing mapping flags.")]
    public Task<CallToolResult> MapIo(string workspaceName, string expectedRevision, string deviceId, ParameterAddress address, string variable, bool createVariable, string idempotencyKey, CancellationToken cancellationToken, int timeoutSeconds = 120)
        => Write(C.IoMap, new(workspaceName, ObjectId: deviceId, Configuration: new(Mapping: new(address, variable, createVariable))), idempotencyKey, expectedRevision, timeoutSeconds, cancellationToken);

    private Task<CallToolResult> Read(string op, string path, string? id, ConfigurationQuery? query, string key, string? revision, int limit, int timeout, CancellationToken token)
        => Submit(queries.SubmitAsync(new(op, new(path, ObjectId: id, MaxObjects: limit, Query: query), key, revision, timeout), token));
    private Task<CallToolResult> Write(string op, EditInput input, string key, string revision, int timeout, CancellationToken token)
        => Submit(edits.SubmitAsync(op, input, key, revision, timeout, token));
    private async Task<CallToolResult> Submit(Task<Result<JobView>> task)
    {
        var result = await task;
        if (result.IsSuccess) signal.Pulse();
        var reply = new JobToolReply("1.2", executor.ExecutionMode, executor.ExecutionMode == "fixture" ? "simulated" : "unverified", false, result.Value, result.Error);
        var json = JsonSerializer.SerializeToElement(reply, WorkerJson.Options);
        return new() { IsError = result.Error is not null, StructuredContent = json, Content = [new TextContentBlock { Text = json.GetRawText() }] };
    }
}
