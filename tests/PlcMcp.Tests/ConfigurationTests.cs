using System.Text.Json;
using PlcMcp.EngineeringService.Configuration;
using PlcMcp.Inovance;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Execution;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.TestHost;
using Xunit;
using C = PlcMcp.Services.Contracts.Configuration.ConfigurationOperations;

namespace PlcMcp.Tests;

public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("Cyclic", "0", "ms", "invalid_task_interval")]
    [InlineData("Cyclic", "10", "minutes", "invalid_task_interval")]
    [InlineData("Freewheeling", "10", "ms", "task_field_not_applicable")]
    [InlineData("Event", null, null, "invalid_task_event")]
    [InlineData("Wrong", "10", "ms", "invalid_task_settings")]
    public void Task_fields_are_validated_before_queueing(string kind, string? interval, string? unit, string error)
    {
        var edit = new EditInput("demo", ObjectId: Guid.NewGuid().ToString(), Configuration: new(Task: new(kind, "2", interval, unit)));
        Assert.Equal(error, ConfigurationValidation.Write(C.TaskUpdate, edit));
    }
    [Fact] public void Action_type_and_io_create_intent_are_explicit()
    {
        var id = Guid.NewGuid().ToString();
        Assert.Equal("member_type_evidence_required", ConfigurationValidation.Read(C.MemberRead, id, new("Action")));
        Assert.Null(ConfigurationValidation.Read(C.MemberRead, id, new("Action", FixtureConfiguration.TypeId)));
        var mapping = new MappingSettings(new(1), "Input", false);
        Assert.Equal("invalid_io_expression", ConfigurationValidation.Write(C.IoMap, new("demo", ObjectId: id, Configuration: new(Mapping: mapping))));
        Assert.Null(ConfigurationValidation.Write(C.IoMap, new("demo", ObjectId: id, Configuration: new(Mapping: mapping with { CreateVariable = true }))));
        Assert.Null(ConfigurationValidation.Write(C.IoMap, new("demo", ObjectId: id, Configuration: new(Mapping: mapping with { Variable = "GVL.Input" }))));
    }
    [Fact] public void Validation_profile_cannot_be_an_arbitrary_command_allowlist()
    {
        var profile = new InovanceValidationProfile("test", "declared-ide", "declared-engine", "profile", [C.TaskRead, EditOperations.CreateProject]);
        profile.Validate(); Assert.True(profile.Allows(C.TaskRead)); Assert.True(profile.Allows(EditOperations.VerifyProject)); Assert.False(profile.Allows(C.TaskCreate));
        Assert.Throws<InvalidOperationException>(() => (profile with { AllowedOperations = ["script.execute"] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (profile with { AllowedOperations = [C.TaskRead, C.TaskRead] }).Validate());
        Assert.Throws<InvalidOperationException>(() => (profile with { EngineVersion = "" }).Validate());
    }
    [Fact] public async Task Tasks_members_and_object_operations_publish_reopened_versions()
    {
        await using var h = await ManagedEditingTests.Harness.Create();
        var initial = await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"));
        var revision = initial.Result!.Value.GetProperty("revision").GetString();
        async Task<JsonElement> Write(string op, EditInput edit)
        {
            var job = await h.Complete(op, edit, revision);
            revision = job.Result!.Value.GetProperty("revision").GetString();
            return job.Result.Value.GetProperty("engineeringResult").GetProperty("configuration");
        }
        var configuration = await Write(C.TaskConfigurationCreate, new("demo", ParentId: h.Fixture.ApplicationId));
        var task = await Write(C.TaskCreate, new("demo", ParentId: Id(configuration), Name: "Cycle", Configuration: new(Task: new("Cyclic", "2", "10", "ms", Watchdog: new(true, "20", "ms", "1")))));
        var taskId = Id(task);
        await Write(C.TaskUpdate, new("demo", ObjectId: taskId, Configuration: new(Task: new("Cyclic", "4", "20", "ms"))));
        await Write(C.TaskCallsSet, new("demo", ObjectId: taskId, Configuration: new(Calls: [new(h.Fixture.ProgramId, "PLC_PRG", "入口")])));
        var rejected = await h.Jobs.SubmitAsync(C.ObjectRename, new("demo", ObjectId: h.Fixture.ProgramId, Name: "Changed", Configuration: new(Object: new("PLC_PRG"))), "referenced", revision, 120, default);
        await h.Dispatcher.RunNextAsync(default);
        Assert.Equal("task_reference_conflict", (await h.Store.GetAsync(rejected.Value!.JobId, default))!.ErrorCode);
        Assert.Equal(revision, (await h.Jobs.GetWorkspaceAsync("demo", default)).Value!.Revision);
        await Write(C.TaskCallsSet, new("demo", ObjectId: taskId, Configuration: new(Calls: [])));
        var method = await Write(C.MemberCreate, new("demo", ParentId: h.Fixture.ProgramId, Name: "Calc", Implementation: "Calc := 1;", Configuration: new(Member: new("Method", ReturnType: "INT"))));
        var changed = await Write(C.MemberUpdate, new("demo", ObjectId: Id(method), Implementation: "Calc := 2;", Configuration: new(Member: new("Method"))));
        Assert.Equal("Calc := 2;", changed.GetProperty("data").GetProperty("implementation").GetString());
        var property = await Write(C.MemberCreate, new("demo", ParentId: h.Fixture.ProgramId, Name: "Value", Configuration: new(Member: new("Property", ReturnType: "INT", Getter: new(Implementation: "Value := x;"), Setter: new(Implementation: "x := Value;")))));
        Assert.Equal("x := Value;", property.GetProperty("data").GetProperty("setter").GetProperty("implementation").GetString());
        var action = await Write(C.MemberCreate, new("demo", ParentId: h.Fixture.ProgramId, Name: "Reset", Implementation: "x := 0;", Configuration: new(Member: new("Action"))));
        await Write(C.MemberUpdate, new("demo", ObjectId: Id(action), Implementation: "x := 1;", Configuration: new(Member: new("Action", ExpectedTypeId: FixtureConfiguration.TypeId))));
        var folder = await Write(C.FolderCreate, new("demo", ParentId: h.Fixture.ApplicationId, Name: "Group", Configuration: new(Object: new())));
        await Write(C.ObjectRename, new("demo", ObjectId: Id(folder), Name: "Renamed", Configuration: new(Object: new("Group"))));
        var other = await Write(C.FolderCreate, new("demo", ParentId: h.Fixture.ApplicationId, Name: "Other", Configuration: new(Object: new())));
        await Write(C.ObjectMove, new("demo", ParentId: Id(other), ObjectId: Id(folder), Configuration: new(Object: new("Renamed"))));
        await Write(C.ObjectDelete, new("demo", ObjectId: Id(method), Configuration: new(Object: new("Calc"))));
        await Write(C.TaskDelete, new("demo", ObjectId: taskId, Configuration: new(Object: new("Cycle"))));
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }
    [Fact] public async Task Dependencies_change_without_mutating_template_or_blocking_later_edits()
    {
        await using var h = await ManagedEditingTests.Harness.Create();
        var originalTemplate = await File.ReadAllTextAsync(h.Descriptor);
        var created = await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"));
        var revision = created.Result!.Value.GetProperty("revision").GetString();
        async Task<JsonElement> Write(string op, EditInput edit)
        {
            var result = (await h.Complete(op, edit, revision)).Result!.Value; revision = result.GetProperty("revision").GetString(); return result;
        }
        var added = await Write(C.LibraryAdd, new("demo", ObjectId: FixtureConfiguration.ManagerId, Configuration: new(Library: new("Extra, 1.0 (Fixture)"))));
        Assert.Contains(added.GetProperty("currentDependencies").GetProperty("libraries").EnumerateArray(), x => x.GetProperty("name").GetString() == "Extra, 1.0 (Fixture)");
        await Write(C.PlaceholderSet, new("demo", ObjectId: FixtureConfiguration.ManagerId, Configuration: new(Library: new("Common", "Base, 1.0 (Fixture)", "Extra, 1.0 (Fixture)"))));
        await Write(C.LibraryRemove, new("demo", ObjectId: FixtureConfiguration.ManagerId, Configuration: new(Library: new("Extra, 1.0 (Fixture)"))));
        var d = await Write(C.DeviceAdd, new("demo", ParentId: FixtureConfiguration.ControllerId, Name: "Input", Configuration: new(Device: new(new(5, "fixture-io", "1.0")))));
        var device = Id(d.GetProperty("engineeringResult").GetProperty("configuration"));
        await Write(C.ParameterWrite, new("demo", ObjectId: device, Configuration: new(Parameter: new(new(1), "42"))));
        await Write(C.IoMap, new("demo", ObjectId: device, Configuration: new(Mapping: new(new(1), "GVL.Input"))));
        await Write(C.DeviceRemove, new("demo", ObjectId: device, Configuration: new(Object: new("Input"))));
        await Write(EditOperations.UpdatePou, new("demo", ObjectId: h.Fixture.ProgramId, Implementation: "counter := 9;"));
        await Write(EditOperations.SaveProject, new("demo"));
        Assert.Equal(originalTemplate, await File.ReadAllTextAsync(h.Descriptor));
        var head = (await h.Jobs.GetWorkspaceAsync("demo", default)).Value!;
        Assert.Single(head.CurrentDependencies!.Devices);
        Assert.DoesNotContain(head.CurrentDependencies.Libraries, x => x.Name == "Extra, 1.0 (Fixture)");
        foreach (var (op, id, query) in new (string, string?, ConfigurationQuery?)[] {
            (C.DeviceTree, null, null), (C.ParameterRead, FixtureConfiguration.ControllerId, null), (C.IoRead, FixtureConfiguration.ControllerId, null),
            (C.LibraryList, FixtureConfiguration.ManagerId, null), (C.LibraryInstalled, null, null), (C.ObjectInspect, "project", null), (C.TaskList, h.Fixture.ApplicationId, null) })
        {
            var queued = await h.ReadJobs.SubmitAsync(new(op, new(head.ProjectPath, ObjectId: id, Query: query), Guid.NewGuid().ToString()), default);
            Assert.True(queued.IsSuccess); await h.Dispatcher.RunNextAsync(default);
            Assert.Equal("Succeeded", (await h.Store.GetAsync(queued.Value!.JobId, default))!.Status);
        }
    }
    private static string Id(JsonElement result) => result.GetProperty("objectId").GetString()!;
}
