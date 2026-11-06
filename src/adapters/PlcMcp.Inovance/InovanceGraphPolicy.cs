using PlcMcp.Core.Workspaces;
using PlcMcp.Services.Contracts.Exchange;

namespace PlcMcp.Inovance;

/// <summary>Conservative CODESYS declaration subset; these checks are not target IDE acceptance.</summary>
public sealed class InovanceGraphPolicy : IPlcGraphPolicy
{
    public string? Validate(GraphicalProgram program)
    {
        if (GraphRules.Validate(program) is { } invalid) return invalid;
        var variables = program.Variables.ToDictionary(v => v.Name, StringComparer.Ordinal);
        foreach (var group in GraphDeclarationRules.Groups(program))
        {
            if (group.Scope == "temp" && (program.PouType == "function" || group.Constant || group.Retention != "default"))
                return "inovance_temporary_declaration_invalid";
            if (group.Scope == "external" && (group.Constant || group.Retention != "default"))
                return "inovance_external_declaration_outside_profile";
            if (group.Constant && group.Scope is not ("local" or "input")) return "inovance_constant_scope_invalid";
            if (group.Retention != "default" && (program.PouType == "function" || group.Scope is "temp" or "external"))
                return "inovance_retention_scope_invalid";
            foreach (var name in group.Variables)
            {
                var variable = variables[name];
                if (group.Scope == "external" && (variable.InitialValue is not null || variable.Initializer is not null || variable.Address is not null))
                    return "inovance_external_declaration_invalid";
                if (variable.Address is null) continue;
                if (group.Retention == "retain") return "inovance_retain_address_conflict";
                if (group.Constant || group.Scope != "local" || program.PouType == "function")
                    return "inovance_address_scope_invalid";
            }
        }
        return null;
    }
}
