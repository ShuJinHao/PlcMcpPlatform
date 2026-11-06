using System.Text.Json;
using System.Text.Json.Nodes;
using PlcMcp.Core.Workspaces;
using PlcMcp.EngineeringService.Batch;
using PlcMcp.EngineeringService.Incremental;
using PlcMcp.Services.Contracts.Batch;
using PlcMcp.Services.Contracts.Configuration;
using PlcMcp.Services.Contracts.Editing;
using PlcMcp.Services.Contracts.Incremental;
using PlcMcp.Services.Contracts.Workers;
using PlcMcp.TestHost;
using Xunit;

namespace PlcMcp.Tests;

public sealed partial class IncrementalTests
{
    private static BatchPlan MotorPlan(string app) => new(app,
    [
        new("folder", ConfigurationOperations.FolderCreate, new(ParentId: app, Name: "Motors", Configuration: new(Object: new()))),
        new("motor", EditOperations.CreatePou, new(ParentId: "@folder", Name: "FB_Motor", PouType: "FunctionBlock", Language: "ST",
            Declaration: "FUNCTION_BLOCK FB_Motor\nVAR\n runningState : BOOL;\n alarmState : BOOL;\nEND_VAR", Implementation: "// motor")),
        new("start", ConfigurationOperations.MemberCreate, new(ParentId: "@motor", Name: "Start", Implementation: "runningState := TRUE;", Configuration: new(Member: new("Method")))),
        new("stop", ConfigurationOperations.MemberCreate, new(ParentId: "@motor", Name: "Stop", Implementation: "runningState := FALSE;", Configuration: new(Member: new("Method")))),
        new("reset", ConfigurationOperations.MemberCreate, new(ParentId: "@motor", Name: "Reset", Implementation: "alarmState := FALSE;", Configuration: new(Member: new("Method")))),
        new("running", ConfigurationOperations.MemberCreate, new(ParentId: "@motor", Name: "Running", Configuration: new(Member: new("Property", ReturnType: "BOOL",
            Getter: new(Implementation: "Running := runningState;"), Setter: new(Implementation: "runningState := Running;"))))),
        new("alarm", ConfigurationOperations.MemberCreate, new(ParentId: "@motor", Name: "Alarm", Configuration: new(Member: new("Property", ReturnType: "BOOL", Getter: new(Implementation: "Alarm := alarmState;"))))),
        new("action", ConfigurationOperations.MemberCreate, new(ParentId: "@motor", Name: "Clear", Implementation: "alarmState := FALSE;", Configuration: new(Member: new("Action")))),
        new("state", EditOperations.CreateDut, new(ParentId: app, Name: "MotorState", Declaration: "TYPE MotorState : (Idle, Running); END_TYPE", DutType: "Enumeration")),
        new("globals", EditOperations.CreateGvl, new(ParentId: app, Name: "GVL_Motor", Declaration: "VAR_GLOBAL\n Enable : BOOL;\nEND_VAR")),
        new("main", EditOperations.CreatePou, new(ParentId: app, Name: "MAIN", PouType: "Program", Language: "ST",
            Declaration: "PROGRAM MAIN\nVAR\n motor : FB_Motor;\nEND_VAR", Implementation: "motor();")),
        new("configuration", ConfigurationOperations.TaskConfigurationCreate, new(ParentId: app)),
        new("task", ConfigurationOperations.TaskCreate, new(ParentId: "@configuration", Name: "Cycle", Configuration: new(Task: new("Cyclic", "1", "20", "ms")))),
        new("calls", ConfigurationOperations.TaskCallsSet, new(ObjectId: "@task", Configuration: new(Calls: [new("@main", "MAIN")]))),
        new("library", ConfigurationOperations.LibraryAdd, new(ObjectId: FixtureConfiguration.ManagerId, Configuration: new(Library: new("Extra, 1.0 (Fixture)"))))
    ]);
    private static async Task<JsonElement> ApplyPlan(ManagedEditingTests.Harness h, Services c, string revision, BatchPlan plan, string status = "Succeeded")
    {
        var preview = await c.Batches.PreviewAsync("demo", revision, plan, default);
        var job = await Finish(h, c, await c.Batches.ApplyAsync("demo", revision, plan, preview.PlanSha256, Guid.NewGuid().ToString(), 600, default), status);
        return job.Result!.Value;
    }
    [Fact] public async Task Motor_members_accessor_queries_and_exact_patches_publish_only_complete_batches()
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h);
        var initial = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"))).Result!.Value.GetProperty("revision").GetString()!;
        var built = await ApplyPlan(h, c, initial, MotorPlan(h.Fixture.ApplicationId));
        var revision = built.GetProperty("revision").GetString()!;
        var batch = built.GetProperty("engineeringResult").GetProperty("batch"); var ids = batch.GetProperty("aliases");
        string Id(string name) => ids.GetProperty(name).GetString()!;
        Assert.Equal("all_applications", batch.GetProperty("compileScope").GetString());
        var query = new IncrementalQuery("demo", revision, h.Fixture.ApplicationId, Collection: "members", ParentId: Id("motor"), PageSize: 2, MaxTextChars: 8);
        var all = new List<ContextObject>(); string? cursor = null;
        do
        {
            var job = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Query, revision, query with { Cursor = cursor }, Guid.NewGuid().ToString(), 600, default));
            var page = job.Result!.Value.Deserialize<EngineeringPage>(WorkerJson.Options)!;
            Assert.Equal(6, page.TotalItems);
            all.AddRange(page.Items.Deserialize<ContextObject[]>(WorkerJson.Options)!); cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(6, all.Select(x => x.ObjectId).Distinct().Count());
        Assert.All(all.Where(x => x.Kind is "Method" or "Property"), x => Assert.Equal(Id("motor"), x.OwnerId));
        var reset = all.Single(x => x.Name == "Reset"); Assert.Equal(TextPatchRules.Hash("alarmState := FALSE;"), reset.Implementation!.Sha256);
        var accessorJob = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Query, revision,
            query with { ParentId = Id("running"), PageSize = 8, MaxTextChars = 8192 }, "accessors", 600, default));
        var accessors = accessorJob.Result!.Value.GetProperty("items").Deserialize<ContextObject[]>(WorkerJson.Options)!;
        var getter = accessors.Single(x => x.Accessor == "Get"); var setter = accessors.Single(x => x.Accessor == "Set");
        Assert.NotEqual(getter.ObjectId, setter.ObjectId); Assert.Equal(Id("running"), getter.OwnerId);
        var actionJob = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Query, revision,
            new("demo", revision, h.Fixture.ApplicationId, Id("action"), SourceQuery: new("Action", FixtureConfiguration.TypeId)), "action", 600, default));
        Assert.Equal("Action", actionJob.Result!.Value.GetProperty("items")[0].GetProperty("kind").GetString());
        var patchPlan = new BatchPlan(h.Fixture.ApplicationId,
        [
            new("reset", IncrementalOperations.Patch, new(ObjectId: Id("reset"), Patch: new("implementation", reset.Implementation.Sha256, "FALSE", "TRUE", "Method"))),
            new("getter", IncrementalOperations.Patch, new(ObjectId: Id("running"), Patch: new("implementation", getter.Implementation!.Sha256, "runningState", "NOT alarmState", "Property", Accessor: "Get"))),
            new("setter", IncrementalOperations.Patch, new(ObjectId: Id("running"), Patch: new("implementation", setter.Implementation!.Sha256, "Running", "FALSE", "Property", Accessor: "Set"))),
            new("action", IncrementalOperations.Patch, new(ObjectId: Id("action"), Patch: new("implementation", TextPatchRules.Hash("alarmState := FALSE;"), "FALSE", "TRUE", "Action", FixtureConfiguration.TypeId)))
        ]);
        var patched = await ApplyPlan(h, c, revision, patchPlan); var next = patched.GetProperty("revision").GetString()!;
        var readBack = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Query, next,
            query with { Revision = next, ParentId = Id("running"), PageSize = 8, MaxTextChars = 8192 }, "readback", 600, default));
        var after = readBack.Result!.Value.GetProperty("items").Deserialize<ContextObject[]>(WorkerJson.Options)!;
        Assert.Equal("Running := NOT alarmState;", after.Single(x => x.Accessor == "Get").Implementation!.Text);
        Assert.Equal("runningState := FALSE;", after.Single(x => x.Accessor == "Set").Implementation!.Text);
        Assert.Equal(3, (await h.ChangesList()).Count(x => x.Stage == "published"));
        await ApplyPlan(h, c, next, patchPlan, "Failed"); // Old hashes may not be reused after publication.
        Assert.Equal(next, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Device_batch_requires_explicit_scope_and_checks_every_application(bool declareScope, bool secondAppFails)
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h);
        var otherApp = Guid.NewGuid().ToString(); var seed = new FixtureConfiguration(h.Fixture).Save();
        seed.Configuration![otherApp] = new JsonObject { ["objectId"] = otherApp, ["name"] = "Other", ["kind"] = "application",
            ["parentId"] = FixtureConfiguration.ControllerId, ["typeId"] = FixtureConfiguration.TypeId, ["compileError"] = secondAppFails };
        await File.WriteAllTextAsync(h.TemplateFile, JsonSerializer.Serialize(seed, WorkerJson.Options));
        var revision = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"))).Result!.Value.GetProperty("revision").GetString()!;
        var plan = new BatchPlan(h.Fixture.ApplicationId,
        [
            new("io", ConfigurationOperations.DeviceAdd, new(ParentId: FixtureConfiguration.ControllerId, Name: "IO", Configuration: new(Device: new(new(4096, "io", "1.0"))))),
            new("parameter", ConfigurationOperations.ParameterWrite, new(ObjectId: "@io", Configuration: new(Parameter: new(new(1), "12")))),
            new("mapping", ConfigurationOperations.IoMap, new(ObjectId: "@io", Configuration: new(Mapping: new(new(1), "GVL.Signal"))))
        ], DeviceScopeIds: declareScope ? [FixtureConfiguration.ControllerId] : null);
        var result = await ApplyPlan(h, c, revision, plan, declareScope && !secondAppFails ? "Succeeded" : "Failed");
        if (declareScope && !secondAppFails)
        {
            var batch = result.GetProperty("engineeringResult").GetProperty("batch");
            Assert.Equal(2, batch.GetProperty("checks").GetArrayLength());
            Assert.Contains(otherApp, batch.GetProperty("affectedApplicationIds").EnumerateArray().Select(x => x.GetString()));
        }
        else Assert.Equal(revision, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
    }
    [Fact] public async Task Deleted_subtree_cannot_be_targeted_by_a_later_alias()
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h);
        var revision = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"))).Result!.Value.GetProperty("revision").GetString()!;
        var plan = new BatchPlan(h.Fixture.ApplicationId,
        [
            new("folder", ConfigurationOperations.FolderCreate, new(ParentId: h.Fixture.ApplicationId, Name: "Group", Configuration: new(Object: new()))),
            new("child", ConfigurationOperations.FolderCreate, new(ParentId: "@folder", Name: "Nested", Configuration: new(Object: new()))),
            new("delete", ConfigurationOperations.ObjectDelete, new(ObjectId: "@folder", Configuration: new(Object: new("Group", true)))),
            new("rename", ConfigurationOperations.ObjectRename, new(ObjectId: "@child", Name: "Stale", Configuration: new(Object: new("Nested"))))
        ]);
        await ApplyPlan(h, c, revision, plan, "Failed");
        Assert.Equal(revision, (await c.Edits.GetWorkspaceAsync("demo", default)).Value!.Revision);
        Assert.False((await h.Store.GetSlotAsync(default)).Blocked);
    }
    [Fact] public void Continuation_is_bound_to_head_revision_scope_and_text_window()
    {
        var query = new IncrementalQuery("demo", "revision", Guid.NewGuid().ToString(), PageSize: 2, ParentId: Guid.NewGuid().ToString());
        var cursor = EngineeringCursor.Encode("head", query, 2);
        Assert.Equal(2, EngineeringCursor.Resume("head", query with { Cursor = cursor }).Offset);
        foreach (var changed in new[] { query with { Revision = "other" }, query with { ParentId = null }, query with { Collection = "members" }, query with { TextOffset = 8 } })
            Assert.Throws<ProjectEditException>(() => EngineeringCursor.Resume("head", changed with { Cursor = cursor }));
        Assert.Throws<ProjectEditException>(() => EngineeringCursor.Resume("new-head", query with { Cursor = cursor }));
        Assert.Throws<ProjectEditException>(() => EngineeringCursor.Resume("head", query with { Cursor = "not a cursor" }));
    }
    [Fact] public async Task Task_and_library_pages_return_remaining_items_and_exclude_other_application_libraries()
    {
        await using var h = await ManagedEditingTests.Harness.Create(); var c = Compose(h);
        var seed = new FixtureConfiguration(h.Fixture).Save();
        var references = seed.Configuration![FixtureConfiguration.ManagerId]!["references"]!.AsArray();
        foreach (var name in new[] { "Base, 1.0 (Fixture)", "Extra, 1.0 (Fixture)" })
            references.Add(new JsonObject { ["managerId"] = FixtureConfiguration.ManagerId, ["name"] = name,
                ["namespace"] = "Fixture", ["isPlaceholder"] = false });
        var otherApp = Guid.NewGuid().ToString(); var otherManager = Guid.NewGuid().ToString();
        seed.Configuration[otherApp] = new JsonObject { ["objectId"] = otherApp, ["name"] = "Other", ["kind"] = "application", ["parentId"] = FixtureConfiguration.ControllerId };
        seed.Configuration[otherManager] = new JsonObject { ["objectId"] = otherManager, ["name"] = "OtherLibraries", ["kind"] = "library_manager", ["parentId"] = otherApp,
            ["references"] = new JsonArray(new JsonObject { ["managerId"] = otherManager, ["name"] = "OtherOnly", ["namespace"] = "Other", ["isPlaceholder"] = false }) };
        await File.WriteAllTextAsync(h.TemplateFile, JsonSerializer.Serialize(seed, WorkerJson.Options));
        var revision = (await h.Complete(EditOperations.CreateProject, new("demo", TemplateId: "sample"))).Result!.Value.GetProperty("revision").GetString()!;
        var built = await ApplyPlan(h, c, revision, new(h.Fixture.ApplicationId,
        [
            new("configuration", ConfigurationOperations.TaskConfigurationCreate, new(ParentId: h.Fixture.ApplicationId)),
            new("first", ConfigurationOperations.TaskCreate, new(ParentId: "@configuration", Name: "First", Configuration: new(Task: new("Cyclic", "1", "20", "ms")))),
            new("second", ConfigurationOperations.TaskCreate, new(ParentId: "@configuration", Name: "Second", Configuration: new(Task: new("Cyclic", "2", "20", "ms"))))
        ]));
        revision = built.GetProperty("revision").GetString()!;
        foreach (var collection in new[] { "tasks", "libraries" })
        {
            var query = new IncrementalQuery("demo", revision, h.Fixture.ApplicationId, PageSize: 1, Collection: collection);
            var first = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Query, revision, query, collection + "-first", 600, default));
            var page = first.Result!.Value.Deserialize<EngineeringPage>(WorkerJson.Options)!;
            Assert.Equal(2, page.TotalItems); Assert.NotNull(page.NextCursor);
            var second = await Finish(h, c, await c.Reads.ReadAsync(IncrementalOperations.Query, revision, query with { Cursor = page.NextCursor }, collection + "-next", 600, default));
            var next = second.Result!.Value.Deserialize<EngineeringPage>(WorkerJson.Options)!;
            Assert.Equal(2, next.TotalItems); Assert.Null(next.NextCursor);
            Assert.NotEqual(page.Items[0].GetRawText(), next.Items[0].GetRawText());
            if (collection == "libraries") Assert.Equal(FixtureConfiguration.ManagerId, next.Items[0].GetProperty("managerId").GetString());
        }
    }
    [Fact] public void External_library_scope_is_explicit_and_device_scope_cannot_authorize_program_writes()
    {
        var app = Guid.NewGuid().ToString(); var device = Guid.NewGuid().ToString(); var manager = Guid.NewGuid().ToString();
        var rows = JsonSerializer.SerializeToElement(new[] {
            new { id = device, parentId = "project", kind = "device" }, new { id = app, parentId = device, kind = "application" },
            new { id = manager, parentId = "project", kind = "library_manager" } }).EnumerateArray().ToArray();
        var plan = new BatchPlan(app, [], DeviceScopeIds: [device]);
        var scope = new BatchScope(plan, rows); scope.ValidateDeclaredScopes();
        Assert.Throws<ProjectEditException>(() => scope.ValidateStep(ConfigurationOperations.LibraryAdd, new("demo", ObjectId: manager)));
        Assert.Throws<ProjectEditException>(() => scope.ValidateStep(ConfigurationOperations.ObjectDelete, new("demo", ObjectId: device)));
        new BatchScope(plan with { LibraryManagerIds = [manager] }, rows).ValidateStep(ConfigurationOperations.LibraryAdd, new("demo", ObjectId: manager));
    }
    [Fact] public void Scope_and_member_patch_guards_reject_unpinned_or_ambiguous_requests()
    {
        var app = Guid.NewGuid().ToString();
        var plan = MotorPlan(app);
        Assert.Equal("invalid_batch_scope", BatchValidation.Validate("demo", plan with { DeviceScopeIds = [app, app] }));
        Assert.NotEqual(BatchValidation.Hash("demo", "r", plan), BatchValidation.Hash("demo", "r", plan with { DeviceScopeIds = [app] }));
        Assert.Equal("invalid_text_patch", IncrementalValidation.Patch(new("demo", ObjectId: app, Patch: new("implementation", new string('a', 64), "a", "b", "Action"))));
        Assert.Equal("invalid_text_patch", IncrementalValidation.Patch(new("demo", ObjectId: app, Patch: new("implementation", new string('a', 64), "a", "b", "Method", Accessor: "Get"))));
    }
}
