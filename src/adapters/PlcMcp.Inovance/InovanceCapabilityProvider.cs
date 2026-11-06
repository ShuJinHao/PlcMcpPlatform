using PlcMcp.Services.Contracts.Capabilities;

namespace PlcMcp.Inovance;

/// <summary>仅登记覆盖目标。没有真实 IDE 证据时，所有工程能力均为 Unverified。</summary>
public sealed class InovanceCapabilityProvider : IPlcCapabilityProvider
{
    public string VendorId => "inovance";
    public IReadOnlyList<CapabilityDescriptor> GetCapabilities() => Array.AsReadOnly(new CapabilityDescriptor[]
    {
        Describe("project.create", OperationRisk.ModifyProject),
        Describe("project.open", OperationRisk.Read),
        Describe("project.close", OperationRisk.ModifyProject),
        Describe("project.save", OperationRisk.ModifyProject),
        Describe("project.save_as", OperationRisk.ModifyProject),
        Describe("project.backup", OperationRisk.ModifyProject),
        Describe("project.tree.read", OperationRisk.Read),
        Describe("pou.create", OperationRisk.ModifyProject),
        Describe("pou.read", OperationRisk.Read),
        Describe("pou.update", OperationRisk.ModifyProject),
        Describe("pou.delete", OperationRisk.ModifyProject),
        Describe("pou.method.manage", OperationRisk.ModifyProject),
        Describe("pou.property.manage", OperationRisk.ModifyProject),
        Describe("pou.action.manage", OperationRisk.ModifyProject),
        Describe("language.st.edit", OperationRisk.ModifyProject),
        Describe("language.ld.edit", OperationRisk.ModifyProject),
        Describe("language.fbd.edit", OperationRisk.ModifyProject),
        Describe("language.sfc.edit", OperationRisk.ModifyProject),
        Describe("language.cfc.edit", OperationRisk.ModifyProject),
        Describe("gvl.manage", OperationRisk.ModifyProject),
        Describe("dut.manage", OperationRisk.ModifyProject),
        Describe("object.move", OperationRisk.ModifyProject),
        Describe("object.rename", OperationRisk.ModifyProject),
        Describe("folder.manage", OperationRisk.ModifyProject),
        Describe("device.tree.read", OperationRisk.Read),
        Describe("device.add", OperationRisk.ModifyProject),
        Describe("device.remove", OperationRisk.ModifyProject),
        Describe("device.parameter.read", OperationRisk.Read),
        Describe("device.parameter.write", OperationRisk.ModifyProject),
        Describe("device.io_mapping", OperationRisk.ModifyProject),
        Describe("device.ethercat_pdo", OperationRisk.ModifyProject),
        Describe("task.read", OperationRisk.Read),
        Describe("task.create", OperationRisk.ModifyProject),
        Describe("task.update", OperationRisk.ModifyProject),
        Describe("task.pou_binding", OperationRisk.ModifyProject),
        Describe("library.list", OperationRisk.Read),
        Describe("library.reference.manage", OperationRisk.ModifyProject),
        Describe("exchange.codesys_xml", OperationRisk.ModifyProject),
        Describe("exchange.plcopen_xml", OperationRisk.ModifyProject),
        Describe("project.compile", OperationRisk.Read),
        Describe("online.connect", OperationRisk.OnlineControl),
        Describe("online.monitor", OperationRisk.OnlineControl),
        Describe("online.download", OperationRisk.OnlineControl),
        Describe("online.start", OperationRisk.OnlineControl),
        Describe("online.stop", OperationRisk.OnlineControl),
    });
    private static CapabilityDescriptor Describe(string id, OperationRisk risk) =>
        new(id, "inovance", "unverified", CapabilityStatus.Unverified, risk,
            "IDE adapter execution is not implemented. Validate against the installed InoProShop version.");
}
