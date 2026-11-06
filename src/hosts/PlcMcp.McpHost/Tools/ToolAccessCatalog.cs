using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.McpHost.Tools;

public sealed record ToolAvailability(string Name, string Kind, string? Operation, bool Available, string? ErrorCode);
public static class ToolAccessCatalog
{
    public static readonly IReadOnlyDictionary<string, string?> Operations = new Dictionary<string, string?>(StringComparer.Ordinal)
    {
        ["plc_batch_get"] = null,
        ["plc_capabilities_list"] = null,
        ["plc_delivery_create"] = DeliveryOperations.Create,
        ["plc_delivery_read"] = null,
        ["plc_device_add"] = ConfigurationOperations.DeviceAdd,
        ["plc_device_get_io_mappings"] = ConfigurationOperations.IoRead,
        ["plc_device_get_parameters"] = ConfigurationOperations.ParameterRead,
        ["plc_device_get_tree"] = ConfigurationOperations.DeviceTree,
        ["plc_device_remove"] = ConfigurationOperations.DeviceRemove,
        ["plc_device_set_io_mapping"] = ConfigurationOperations.IoMap,
        ["plc_device_set_parameter"] = ConfigurationOperations.ParameterWrite,
        ["plc_dut_create"] = EditOperations.CreateDut,
        ["plc_dut_read"] = EngineeringOperations.ReadDut,
        ["plc_dut_update"] = EditOperations.UpdateDut,
        ["plc_exchange_read"] = null,
        ["plc_exchange_stage"] = null,
        ["plc_exchange_upload_abort"] = null,
        ["plc_exchange_upload_append"] = null,
        ["plc_exchange_upload_begin"] = null,
        ["plc_exchange_upload_complete"] = null,
        ["plc_exchange_validate"] = null,
        ["plc_folder_create"] = ConfigurationOperations.FolderCreate,
        ["plc_graph_decode"] = null,
        ["plc_graph_generate"] = null,
        ["plc_gvl_create"] = EditOperations.CreateGvl,
        ["plc_gvl_read"] = EngineeringOperations.ReadGvl,
        ["plc_gvl_update"] = EditOperations.UpdateGvl,
        ["plc_job_cancel"] = null,
        ["plc_job_get"] = null,
        ["plc_job_get_result"] = null,
        ["plc_job_reconcile"] = null,
        ["plc_library_add"] = ConfigurationOperations.LibraryAdd,
        ["plc_library_list"] = ConfigurationOperations.LibraryList,
        ["plc_library_list_installed"] = ConfigurationOperations.LibraryInstalled,
        ["plc_library_remove"] = ConfigurationOperations.LibraryRemove,
        ["plc_library_set_placeholder"] = ConfigurationOperations.PlaceholderSet,
        ["plc_object_delete"] = ConfigurationOperations.ObjectDelete,
        ["plc_object_inspect"] = ConfigurationOperations.ObjectInspect,
        ["plc_object_move"] = ConfigurationOperations.ObjectMove,
        ["plc_object_rename"] = ConfigurationOperations.ObjectRename,
        ["plc_pou_create"] = EditOperations.CreatePou,
        ["plc_pou_graph_read"] = ExchangeOperations.ReadGraph,
        ["plc_pou_graph_write"] = ExchangeOperations.WriteGraph,
        ["plc_pou_member_create"] = ConfigurationOperations.MemberCreate,
        ["plc_pou_member_read"] = ConfigurationOperations.MemberRead,
        ["plc_pou_member_update"] = ConfigurationOperations.MemberUpdate,
        ["plc_pou_read"] = EngineeringOperations.ReadProgram,
        ["plc_pou_update"] = EditOperations.UpdatePou,
        ["plc_project_adopt"] = IncrementalOperations.Adopt,
        ["plc_project_adopt_preview"] = null,
        ["plc_project_batch_apply"] = BatchOperations.Apply,
        ["plc_project_batch_preview"] = null,
        ["plc_project_compile"] = EngineeringOperations.Compile,
        ["plc_project_context"] = IncrementalOperations.Context,
        ["plc_project_create"] = EditOperations.CreateProject,
        ["plc_project_diff"] = IncrementalOperations.Diff,
        ["plc_project_export"] = ExchangeOperations.Export,
        ["plc_project_get_structure"] = EngineeringOperations.Structure,
        ["plc_project_history"] = null,
        ["plc_project_import"] = ExchangeOperations.Import,
        ["plc_project_objects_read"] = IncrementalOperations.Query,
        ["plc_project_open"] = EngineeringOperations.Open,
        ["plc_project_restore"] = IncrementalOperations.Restore,
        ["plc_project_save"] = EditOperations.SaveProject,
        ["plc_runtime_get_status"] = null,
        ["plc_task_configuration_create"] = ConfigurationOperations.TaskConfigurationCreate,
        ["plc_task_create"] = ConfigurationOperations.TaskCreate,
        ["plc_task_delete"] = ConfigurationOperations.TaskDelete,
        ["plc_task_list"] = ConfigurationOperations.TaskList,
        ["plc_task_read"] = ConfigurationOperations.TaskRead,
        ["plc_task_set_calls"] = ConfigurationOperations.TaskCallsSet,
        ["plc_task_update"] = ConfigurationOperations.TaskUpdate,
        ["plc_templates_list"] = null,
        ["plc_workspace_get"] = null,
    };
    public static OperationError? Check(string name, IEngineeringExecutor executor)
    {
        if (!Operations.TryGetValue(name, out var operation)) return new("tool_access_not_registered", "No access rule for this tool.");
        return operation is null ? null : executor.CheckAvailability(operation);
    }
    public static ToolAvailability[] Inspect(IEngineeringExecutor executor)
    {
        var operations = executor.InspectAvailability(Operations.Values.OfType<string>());
        return Operations.Select(entry => new ToolAvailability(entry.Key, entry.Value is null ? "local" : "ide", entry.Value,
            entry.Value is null || operations[entry.Value] is null, entry.Value is null ? null : operations[entry.Value]?.Code)).ToArray();
    }
}
