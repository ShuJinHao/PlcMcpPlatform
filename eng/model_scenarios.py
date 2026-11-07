"""Representative model tasks and independent server-result assertions.

These checks intentionally do not accept a model's prose as engineering evidence.
"""
import re

SCENARIOS = ('repair', 'graphics', 'members', 'configuration', 'incremental', 'declarations')


def walk(value):
    if isinstance(value, dict):
        yield value
        for child in value.values(): yield from walk(child)
    elif isinstance(value, list):
        for child in value: yield from walk(child)


def prompt(scenario, workspace, template, app, mode, variables):
    common = f'''Use only the plc MCP tools. This is an authorized isolated {mode} regression in workspace {workspace}.
Read runtime status and tool capabilities first. Use template {template}, Application {app}; discover object IDs from tools.
Only operate on this workspace; no shell, filesystem or online PLC tools. All writes in this workspace are authorized.
Use individual engineering write tools except where the task explicitly asks for a text.patch batch.
Poll every submitted job until terminal. Stop on OutcomeUnknown or unconfirmed cancellation, never retry uncertain writes.
Use current managed revisions for writes, file revisions for reads, and new idempotency keys for changed requests.
Create this workspace from the template unless the task below requires adoption.
After all edits, explicitly read back each changed object/configuration at the final head, compile the Application,
and create a delivery for that exact final m2 revision. Never print ZIP/base64. Return artifact ID and SHA256.
Label fixture compilation as simulated. Do not claim real vendor compatibility.
'''
    tasks = {
        'graphics': '''Create and then modify three graph-v2 programs using plc_graph_generate and individual plc_pou_graph_write calls.
1. ModelLD: BOOL Enable and Running, left rail -> Enable contact -> Running coil -> right rail. Initially ordinary coil; after reading it back update the coil to negated=true.
2. ModelFBD: DINT Result; inVariable expression 1 connected to outVariable Result. Initially 1; read back then change the inVariable expression to 2.
3. ModelSFC: DINT Counter; initial step Start -> TRUE transition -> step Run -> TRUE transition -> jumpStep Start.
Associate an N action Mark with Run; its ST body initially Counter := Counter + 1;. Read back, then change Mark to Counter := Counter + 2;.
Use graph-v2. Obtain the current graph hash from reads for replacement.
Use named variableGroups and give every variable documentation='model regression'. No independent child objects are allowed.
After all six writes, read all three programs again with plc_pou_graph_read at the FINAL projectPath and package that revision.
''',
        'declarations': '''Create an FBD Program ModelDeclarations assigning State.Count to DINT Result.
State is an inline structure, with members in this order: Count (DINT, default 1), Samples (INT array 0..2, initialized to 1,2,3), Nested (inline structure containing BOOL Enabled, default TRUE).
Cursor is a pointer to DINT, and Cursors is an array 0..1 of pointers to INT. Keep their address values unspecified.
RetainedCount is DINT in a separate local group marked RETAIN and PERSISTENT together. All other variables are in a local NON_PERSISTENT group.
State has an XHTML formatted comment containing a paragraph and bold text 'preserve format'. Use the standard XHTML namespace.
Read the first program back, then change Count's member default to 2, Cursor's target type to INT, and the bold comment text to 'format updated'.
Preserve member order, Samples initializers, Nested, Cursors, and both groups' persistence properties.
Read the FINAL program back, query its graph diff from the first written revision, compile and deliver the final revision.
Persistence flags and pointer declarations here are offline representations; do not claim persistent-list association or real address/runtime validation.
''',
        'members': '''Create a FunctionBlock ModelFB with DINT Counter and ST body Counter := Counter + 1;.
Create GVL ModelGlobals with VAR_GLOBAL gModel : DINT := 1; END_VAR and DUT ModelType as TYPE ModelType : DINT; END_TYPE (Alias, baseType DINT).
Under ModelFB create Method Bump returning DINT (METHOD Bump : DINT, body Bump := Counter;),
Property Count of type DINT with a getter Count := Counter; and setter Counter := Count;,
and Action Reset with ST body Counter := 0;. Use the actual type ID from inspect for Action reads/updates.
Then modify GVL gModel initial value to 2; change the Method body to Bump := Counter + 2;,
Property getter to Count := Counter + 2; preserving its setter, and Action body to Counter := 2;.
Read the GVL, DUT, Method, Property (both accessors) and Action at the FINAL projectPath before delivery.
''',
        'configuration': f'''Create cyclic task ModelCycle with interval 10 ms and bind existing program {variables['programName']} ({variables['programId']}).
Create Task Configuration only if needed; then update the task interval to 20 ms, preserving its call binding.
Inspect device tree and library manager. Add device ModelIO under controller {variables['controllerId']} with settings {variables['deviceSettings']}.
On the added device set parameter address {variables['parameterAddress']} to value {variables['parameterValue']!r}
and IO mapping address {variables['mappingAddress']} to variable {variables['programName']}.counter.
Add installed library {variables['libraryName']!r} to manager {variables['libraryManagerId']}.
At FINAL projectPath read tasks, device tree, the new device parameters and IO mappings, and libraries. Verify all requested values.
PDO layout editing is not implemented: do not fabricate a PDO tool or call IO mapping PDO acceptance.
''',
        'incremental': f'''Instead of creating from a template, preview and adopt this saved import source into the new workspace: {variables['adoptionSource']}.
Read context and remember the base managed revision and complete implementation/hash of {variables['programName']} ({variables['programId']}).
Use a previewed text.patch batch with that hash and exact original text to change its body to counter := counter + 7;.
Read the changed source, query a diff from base to edited revision, and read history. Restore base as a NEW revision using plc_project_restore.
Verify the original PLC_PRG body is restored at the new revision.
Then generate a graph-v2 FBD Program ModelTransfer with DINT TransferValue, an inVariable expression 42 connected to outVariable TransferValue.
Create it using plc_pou_graph_write. Call plc_project_export on ONLY the actual ModelTransfer object to PLCopen, poll its job, and record THAT exported artifact.
Delete this POU and call plc_project_import on the EXPORTED artifact under the Application using reject conflicts. Poll the import job.
After import, call plc_workspace_get for the final projectPath. Call plc_pou_graph_read on the newly imported POU's actual ID,
and plc_pou_read on the original program's ID at this FINAL projectPath. A graph_decode call only examines an artifact and does not prove project readback.
Do not finish until project_export, project_import, final pou.graph.read, final pou.read and exact-final-revision delivery have all succeeded.
Then compile and deliver the final revision.
'''
    }
    return common + tasks[scenario]


def prove(scenario, calls, jobs, head, workspace, mode, variables):
    for call in calls:
        if call['input'].get('workspaceName', workspace) != workspace: raise ValueError('workspace_scope_mismatch')
    if any(j['status'] in ('Queued', 'Running', 'CancelRequested', 'OutcomeUnknown') for j in jobs): raise ValueError('job_outcome_unresolved')
    if any(j.get('executionMode') != mode for j in jobs): raise ValueError('job_mode_mismatch')
    succeeded = [j for j in jobs if j['status'] == 'Succeeded']
    operations = {j['operation'] for j in succeeded}
    created = 'project.adopt' if scenario == 'incremental' else 'project.create'
    if created not in operations: raise ValueError('workspace_creation_not_observed')
    filename = str(head['projectPath']).replace('\\', '/').split('/')[-1]
    digest = next(f['sha256'] for f in head['files'] if f['path'] == filename)
    reads = [j for j in succeeded if j.get('result', {}).get('revision') == digest]
    def rows(operation):
        return [row for j in reads if j['operation'] == operation for row in walk(j['result'])]
    def has(operation, **wanted):
        return any(all(row.get(k) == v for k, v in wanted.items()) for row in rows(operation))
    def require(condition, code):
        if not condition: raise ValueError(code)
    if scenario == 'graphics':
        languages = dict(ModelLD='LD', ModelFBD='FBD', ModelSFC='SFC')

        def graph_record(job, name):
            result = job.get('result') or {}
            if job.get('operation') == 'pou.graph.write':
                if result.get('published') is not True: return None
                result = result.get('engineeringResult') or {}
            elif job.get('operation') != 'pou.graph.read': return None
            exchange = result.get('exchange') or {}
            graph = exchange.get('graph') or {}
            program = graph.get('program') or {}
            identities = exchange.get('objectIds')
            if (program.get('name') != name or program.get('language') != languages[name]
                    or not isinstance(graph.get('semanticSha256'), str) or not graph['semanticSha256']
                    or not isinstance(identities, list) or len(identities) != 1
                    or not isinstance(identities[0], str) or not identities[0]): return None
            return exchange

        def connected(source, target):
            return source.get('id') is not None and any(
                connection.get('sourceId') == source['id']
                for pin in target.get('inputs') or [] for connection in pin.get('connections') or [])

        def expected_state(program, name, initial):
            nodes = program.get('nodes') or []
            if name == 'ModelLD':
                coils = [n for n in nodes if n.get('kind') == 'coil' and n.get('expression') == 'Running']
                return (len(coils) == 1 and coils[0].get('negated', False) is (not initial)
                        and coils[0].get('storage', 'none') == 'none' and coils[0].get('edge', 'none') == 'none')
            if name == 'ModelFBD':
                targets = [n for n in nodes if n.get('kind') == 'outVariable' and n.get('expression') == 'Result']
                if len(targets) != 1: return False
                sources = [n for n in nodes if n.get('kind') == 'inVariable' and connected(n, targets[0])]
                return len(sources) == 1 and sources[0].get('expression') == ('1' if initial else '2')
            actions = [a for a in program.get('actions') or [] if a.get('name') == 'Mark']
            if (len(actions) != 1 or not isinstance(actions[0].get('implementation'), str)
                    or not re.fullmatch(r'\s*Counter\s*:=\s*Counter\s*\+\s*'+('1' if initial else '2')+r'\s*;\s*', actions[0]['implementation'])):
                return False
            steps = [n for n in nodes if n.get('kind') == 'step' and n.get('name') == 'Run']
            links = [(node, association) for node in nodes if node.get('kind') == 'actionBlock'
                     for association in (node.get('associations') or [dict(action=node.get('action'), qualifier=node.get('qualifier', 'N'))])
                     if association.get('action') == 'Mark']
            return len(steps) == 1 and len(links) == 1 and links[0][1].get('qualifier', 'N') == 'N' and connected(steps[0], links[0][0])

        def observed_job(call):
            # Keep identity, status and graph inside one actual JobToolReply.
            # Walking arbitrary output rows can accidentally combine unrelated jobs.
            output = call.get('output')
            if call.get('status') != 'completed' or not isinstance(output, dict) or output.get('error'): return None
            job = output.get('job')
            return job if isinstance(job, dict) else None

        server_jobs = {j['jobId']: j for j in succeeded if isinstance(j.get('jobId'), str)}
        for name, language in languages.items():
            final_reads = [record for j in reads if (record := graph_record(j, name)) is not None]
            require(bool(final_reads), 'final_graph_readback_missing')
            require(any(expected_state(r['graph']['program'], name, False) for r in final_reads), language.lower()+'_modification_missing')
            submitted = []
            seen = set()
            for index, call in enumerate(calls):
                if call['name'] != 'plc_pou_graph_write': continue
                observed = observed_job(call)
                if not observed or observed.get('operation') != 'pou.graph.write' or observed.get('status') not in ('Queued', 'Running', 'Succeeded'): continue
                identity = observed.get('jobId')
                job = server_jobs.get(identity)
                if not job or job.get('operation') != 'pou.graph.write' or identity in seen or (record := graph_record(job, name)) is None: continue
                seen.add(identity); submitted.append((index, call, record))
            require(len(submitted) >= 2, 'graph_creation_and_modification_missing')
            first_index, first_call, first = submitted[0]
            initial_hash = first['graph']['semanticSha256']; initial_id = first['objectIds'][0]
            require(first_call['input'].get('objectId') is None and first_call['input'].get('expectedGraphSha256') is None
                    and expected_state(first['graph']['program'], name, True), 'graph_initial_state_missing')

            modifications = []
            for index, call, record in submitted[1:]:
                graph = record['graph']; target_id = record['objectIds'][0]
                if (call['input'].get('objectId') != initial_id or call['input'].get('expectedGraphSha256') != initial_hash
                        or graph['semanticSha256'] == initial_hash or not expected_state(graph['program'], name, False)): continue
                # XML replacement can assign a new object GUID. Require its explicit
                # old-to-new mapping rather than treating an equal name as identity.
                if target_id != initial_id and not any(r.get('oldObjectId') == initial_id and r.get('objectId') == target_id
                                                       and r.get('name') == name for r in record.get('report') or []): continue
                matching = [r for r in final_reads if r['objectIds'] == record['objectIds'] and r['graph'] == graph]
                if matching: modifications.append((index, matching))
            require(bool(modifications), 'graph_modification_evidence_missing')

            observed_initial = False
            for index, call in enumerate(calls):
                if call['name'] not in ('plc_job_get', 'plc_job_get_result', 'plc_pou_graph_read'): continue
                if not first_index < index or not any(index < modification_index for modification_index, _ in modifications): continue
                observed = observed_job(call)
                if not observed or observed.get('status') != 'Succeeded' or observed.get('operation') != 'pou.graph.read': continue
                identity = observed.get('jobId'); job = server_jobs.get(identity)
                if not job or job.get('operation') != 'pou.graph.read': continue
                if call['name'] == 'plc_pou_graph_read':
                    if call['input'].get('objectId') != initial_id: continue
                elif call['input'].get('jobId') != identity: continue
                record = graph_record(job, name); received = graph_record(observed, name)
                if (record is None or received is None or record['objectIds'] != [initial_id]
                        or received['objectIds'] != record['objectIds'] or received['graph'] != record['graph']
                        or observed.get('result', {}).get('revision') != job.get('result', {}).get('revision')
                        or record['graph']['semanticSha256'] != initial_hash
                        or not expected_state(record['graph']['program'], name, True)): continue
                observed_initial = True
            require(observed_initial, 'graph_initial_read_not_observed_before_modification')
            require(any(all(p.get('variableGroups') and all(v.get('documentation') == 'model regression' for v in p.get('variables', []))
                            for p in [r['graph']['program'] for r in matching]) for _, matching in modifications), 'graph_declaration_readback_missing')
    elif scenario == 'declarations':
        programs = [row['program'] for row in rows('pou.graph.read') if isinstance(row.get('program'), dict) and row['program'].get('name') == 'ModelDeclarations']
        require(bool(programs), 'declaration_final_readback_missing')
        program = programs[-1]
        def check_program(program, initial=False):
            values = {v['name']:v for v in program['variables']}
            require({'State', 'Cursor', 'Cursors', 'RetainedCount', 'Result'} <= set(values), 'declaration_variables_missing')
            state = values['State']; members = state.get('structMembers', [])
            require(state.get('type') == 'STRUCT' and [m['name'] for m in members] == ['Count', 'Samples', 'Nested'], 'structure_member_order_mismatch')
            require(members[0]['type'].get('type') == 'DINT' and members[0].get('initialValue') == ('1' if initial else '2'), 'structure_member_change_missing')
            samples = members[1]
            require(samples['type'].get('type') == 'INT' and samples['type'].get('dimensions') == [dict(lower=0, upper=2)], 'structure_array_type_missing')
            items = samples.get('initializer', {}).get('items', [])
            expanded = [i.get('value', {}).get('value') for i in items for _ in range(min(i.get('repeat', 1), 4))]
            require(expanded == ['1', '2', '3'], 'structure_array_initializer_missing')
            nested = members[2]['type']
            require(nested.get('type') == 'STRUCT' and len(nested.get('structMembers', [])) == 1 and nested['structMembers'][0].get('name') == 'Enabled'
                    and nested['structMembers'][0].get('type', {}).get('type') == 'BOOL' and nested['structMembers'][0].get('initialValue', '').upper() == 'TRUE', 'nested_structure_missing')
            require(values['Cursor'].get('type') == 'POINTER' and values['Cursor'].get('pointerTo', {}).get('type') == ('DINT' if initial else 'INT'), 'pointer_change_missing')
            require(values['Cursors'].get('type') == 'POINTER' and values['Cursors'].get('pointerTo', {}).get('type') == 'INT'
                    and values['Cursors'].get('dimensions') == [dict(lower=0, upper=1)], 'pointer_array_missing')
            groups = program.get('variableGroups', [])
            require(any(g.get('retention') == 'retain' and g.get('persistence') == 'persistent' and g.get('variables') == ['RetainedCount'] for g in groups), 'persistence_combination_missing')
            require(any(g.get('persistence') == 'nonPersistent' and set(g.get('variables', [])) == {'State', 'Cursor', 'Cursors', 'Result'} for g in groups), 'nonpersistent_group_missing')
            import xml.etree.ElementTree as ET
            try: comment = ET.fromstring(state.get('documentationXhtml', ''))
            except ET.ParseError: raise ValueError('formatted_comment_missing')
            require(any(e.tag in ('{http://www.w3.org/1999/xhtml}strong', '{http://www.w3.org/1999/xhtml}b') and ''.join(e.itertext()) == ('preserve format' if initial else 'format updated') for e in comment.iter()), 'formatted_comment_change_missing')
        check_program(program)
        # Associate independently fetched server results with the observed CLI calls.
        # Graph writes wrap their exchange result under engineeringResult; reads do not.
        def graph_record(job):
            return next((row for row in walk(job.get('result', {}))
                         if isinstance(row.get('graph'), dict) and row['graph'].get('program', {}).get('name') == 'ModelDeclarations'), None)
        writes = {j['jobId']: (j, graph_record(j)) for j in succeeded
                  if j['operation'] == 'pou.graph.write' and j.get('result', {}).get('published') is True and graph_record(j)}
        submitted = []
        for index, call in enumerate(calls):
            if call['name'] != 'plc_pou_graph_write': continue
            identifiers = {row.get('jobId') for row in walk(call.get('output'))}
            for identity in identifiers & writes.keys(): submitted.append((index, identity))
        require(len(submitted) >= 2 and 'project.diff' in operations, 'declaration_modification_evidence_missing')
        first_index, first_id = submitted[0]
        first_job, first_exchange = writes[first_id]
        first_graph = first_exchange['graph']; initial_hash = first_graph.get('semanticSha256')
        require(isinstance(initial_hash, str) and bool(initial_hash), 'declaration_initial_state_missing')
        try: check_program(first_graph['program'], initial=True)
        except (ValueError, KeyError, TypeError): raise ValueError('declaration_initial_state_missing')
        final_hashes = {row.get('semanticSha256') for row in rows('pou.graph.read')
                        if isinstance(row.get('program'), dict) and row['program'].get('name') == 'ModelDeclarations'}
        final_writes = [(index, identity) for index, identity in submitted if index > first_index
                        and writes[identity][1]['graph'].get('semanticSha256') in final_hashes
                        and writes[identity][1]['graph'].get('semanticSha256') != initial_hash]
        require(bool(final_writes), 'declaration_modification_evidence_missing')
        # A job existing on the server is insufficient: the model must receive its
        # successful initial read result before submitting the final modification.
        initial_reads = {j['jobId'] for j in succeeded if j['operation'] == 'pou.graph.read'
                         and (record := graph_record(j)) is not None
                         and record['graph'].get('semanticSha256') == initial_hash
                         and record.get('objectIds') and record.get('objectIds') == first_exchange.get('objectIds')}
        observed = False
        for index, call in enumerate(calls):
            if call['name'] not in ('plc_job_get', 'plc_job_get_result', 'plc_pou_graph_read'): continue
            identifiers = {call.get('input', {}).get('jobId')} | {row.get('jobId') for row in walk(call.get('output'))}
            if not identifiers & initial_reads or not any(row.get('status') == 'Succeeded' for row in walk(call.get('output'))): continue
            if not any(row.get('semanticSha256') == initial_hash and isinstance(row.get('program'), dict) for row in walk(call.get('output'))): continue
            if first_index < index and any(index < write_index for write_index, _ in final_writes): observed = True
        require(observed, 'declaration_initial_read_not_observed_before_modification')
        require(any(row.get('afterSemanticSha256') in final_hashes and row.get('beforeSemanticSha256') == initial_hash
                    and row['beforeSemanticSha256'] != row['afterSemanticSha256'] and row.get('totalChanges', 0) >= 2
                    for j in succeeded if j['operation'] == 'project.diff' for row in walk(j['result'])), 'declaration_final_diff_missing')
    elif scenario == 'members':
        require({'gvl.create', 'gvl.update', 'dut.create', 'member.create', 'member.update'} <= operations, 'member_operations_missing')
        require(any('gModel' in row.get('declaration', '') and re.search(r':=\s*2\s*;', row['declaration']) for row in rows('gvl.read')), 'gvl_readback_missing')
        require(any('ModelType' in row.get('declaration', '') for row in rows('dut.read')), 'dut_readback_missing')
        implementations = [re.sub(r'\s+', '', row['implementation']) for row in rows('member.read') if isinstance(row.get('implementation'), str)]
        require(all(body in implementations for body in ('Bump:=Counter+2;', 'Count:=Counter+2;', 'Counter:=Count;', 'Counter:=2;')), 'member_accessor_readback_missing')
    elif scenario == 'configuration':
        require({'device.add', 'device.parameter.write', 'device.io.map', 'task.create', 'task.update', 'task.calls.set', 'library.add'} <= operations, 'configuration_operations_missing')
        require(has('device.tree.read', name='ModelIO'), 'device_readback_missing')
        require(has('library.list', name=variables['libraryName']), 'library_readback_missing')
        require(any(str(row.get('value')) == variables['parameterValue'] for row in rows('device.parameters.read')), 'parameter_readback_missing')
        require(any(row.get('variable') == variables['programName']+'.counter' for row in rows('device.io.read')), 'io_mapping_readback_missing')
        require(has('task.list', name='ModelCycle'), 'task_readback_missing')
        require(any(str(row.get('interval')) == '20' and row.get('intervalUnit') == 'ms' for row in rows('task.list')), 'task_interval_readback_missing')
    else:
        require({'project.context', 'project.batch', 'project.diff', 'project.restore', 'project.export', 'project.import'} <= operations, 'incremental_exchange_operations_missing')
        require(any(row.get('name') == 'ModelTransfer' and any(n.get('kind') == 'inVariable' and n.get('expression') == '42' for n in row.get('nodes', []))
                    for row in rows('pou.graph.read')), 'import_readback_missing')
        initial = [row for j in succeeded if j['operation'] == 'project.context' for row in walk(j['result'])
                   if row.get('objectId') == variables['programId'] and isinstance(row.get('implementation'), dict)]
        require(bool(initial), 'adopted_source_evidence_missing')
        original = initial[0]['implementation']['text'].replace('\r\n', '\n')
        require(any(row.get('objectId') == variables['programId'] and row.get('implementation', '').replace('\r\n', '\n') == original for row in rows('pou.read')), 'restore_source_readback_missing')
    packages = [row['artifact'] for j in succeeded if j['operation'] == 'project.package' for row in walk(j['result'])
                if isinstance(row.get('artifact'), dict)]
    matches = [a for a in packages if a.get('revision') == head['revision'] and a.get('workspaceName') == workspace and a.get('executionMode') == mode]
    require(bool(matches), 'exact_final_delivery_missing')
    return matches[-1]
