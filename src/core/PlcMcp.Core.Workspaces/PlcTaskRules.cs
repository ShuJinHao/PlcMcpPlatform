namespace PlcMcp.Core.Workspaces;

public static class PlcTaskRules
{
    public static bool ValidKind(string? kind) => kind is "Cyclic" or "Freewheeling" or "Event" or "ExternalEvent" or "Status" or "ParentSynchron";
    public static bool UsesInterval(string kind) => kind is "Cyclic" or "ExternalEvent";
    public static bool ValidUnit(string? unit) => unit is "us" or "ms" or "s";
    public static bool PositiveNumber(string? value) => decimal.TryParse(value, System.Globalization.NumberStyles.AllowDecimalPoint,
        System.Globalization.CultureInfo.InvariantCulture, out var number) && number > 0;
    public static bool ValidPriority(string value) => uint.TryParse(value, System.Globalization.NumberStyles.None,
        System.Globalization.CultureInfo.InvariantCulture, out _);
}
