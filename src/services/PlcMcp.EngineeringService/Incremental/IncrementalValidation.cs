using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Incremental;

namespace PlcMcp.EngineeringService.Incremental;

public static class IncrementalValidation
{
    public static bool Revision(string? value) => value is { Length: 100 } && value.StartsWith("m2:", StringComparison.Ordinal)
        && Guid.TryParseExact(value.Substring(3, 32), "N", out _) && value[35] == ':' && value[36..].All(Uri.IsHexDigit);
    public static bool Id(string? value) => Guid.TryParse(value, out var id) && id != Guid.Empty;
    public static string? Patch(EditInput input)
    {
        var patch = input.Patch;
        return !Id(input.ObjectId) || patch is null || patch.Section is not ("declaration" or "implementation")
            || patch.ExpectedSha256 is not { Length: 64 } || !patch.ExpectedSha256.All(Uri.IsHexDigit)
            || patch.OldText is not { Length: > 0 and <= 65536 } || patch.NewText is null || patch.NewText.Length > 65536
            || patch.OldText.Contains('\0') || patch.NewText.Contains('\0')
            || patch.MemberKind is not (null or "Method" or "Property" or "Action")
            || patch.ExpectedTypeId is not null && !Id(patch.ExpectedTypeId)
            || patch.MemberKind == "Action" && (!Id(patch.ExpectedTypeId) || patch.Section == "declaration")
            || patch.MemberKind is null && (patch.ExpectedTypeId is not null || patch.Accessor is not null)
            || patch.Accessor is not (null or "Get" or "Set") || patch.Accessor is not null && patch.MemberKind != "Property"
            || patch.MemberKind == "Property" && patch.Accessor is null && patch.Section == "implementation"
            || input.Declaration is not null || input.Implementation is not null || input.Configuration is not null
            || input.Exchange is not null || input.ParentId is not null ? "invalid_text_patch" : null;
    }
    public static string[] Required(string op) => op switch
    {
        IncrementalOperations.Adopt => [EditOperations.VerifyProject],
        IncrementalOperations.Restore => [EditOperations.VerifyProject, EngineeringOperations.Structure, ConfigurationOperations.TaskList, EngineeringOperations.Compile],
        IncrementalOperations.Query or IncrementalOperations.Context => [EngineeringOperations.Structure, IncrementalOperations.Source, ConfigurationOperations.TaskList, ConfigurationOperations.LibraryList],
        _ => []
    };
}
