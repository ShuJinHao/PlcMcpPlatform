using System.Text.Json;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Delivery;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Exchange;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.SharedKernel.Results;

namespace PlcMcp.Services.Contracts.Execution;

public static class CompositeRequirements
{
    public static string[] Dependencies(string operation) => operation switch
    {
        EditOperations.CreateProject or EditOperations.SaveProject or IncrementalOperations.Adopt => [EditOperations.VerifyProject],
        IncrementalOperations.Restore or BatchOperations.Apply => [EditOperations.VerifyProject, EngineeringOperations.Structure, ConfigurationOperations.TaskList, EngineeringOperations.Compile],
        DeliveryOperations.Create => [ConfigurationOperations.TaskList, EngineeringOperations.Compile],
        IncrementalOperations.Query or IncrementalOperations.Context => [EngineeringOperations.Structure, IncrementalOperations.Source, ConfigurationOperations.TaskList, ConfigurationOperations.LibraryList, ConfigurationOperations.MemberRead],
        IncrementalOperations.Diff => [EngineeringOperations.Structure, IncrementalOperations.Source, ExchangeOperations.ReadGraph],
        IncrementalOperations.Patch => [IncrementalOperations.Source, ConfigurationOperations.MemberRead, ConfigurationOperations.MemberUpdate, EditOperations.UpdatePou, EditOperations.UpdateGvl, EditOperations.UpdateDut],
        _ => []
    };
}
