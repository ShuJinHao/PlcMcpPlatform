import copy
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

ENG=Path(__file__).resolve().parents[2]/'eng'
sys.path.insert(0,str(ENG))
spec=importlib.util.spec_from_file_location('model_regression',ENG/'model-regression.py')
model=importlib.util.module_from_spec(spec);spec.loader.exec_module(model)


class ModelRegressionTests(unittest.TestCase):
    def test_metrics_read_codes_from_cli_error_json_without_leaking_error_text(self):
        def event(error):return json.dumps(dict(type='tool_use',part=dict(tool='plc_plc_graph_generate',state=dict(status='error',error=error))))
        raw='\n'.join([event(json.dumps(dict(error=dict(code='invalid_graph_definition',message='private source')))),
                       event('provider detail must never be copied'),
                       event(json.dumps(dict(error=dict(code='unsafe-code/secret',message='secret'))))])
        result=model.observed_metrics(raw)
        self.assertEqual(result['invalidToolCalls'],3)
        self.assertEqual(result['failedToolCalls'],3)
        self.assertEqual(result['toolRejectionCodes'],dict(invalid_graph_definition=1))
        for secret in ('private source','provider detail','unsafe-code/secret'):
            self.assertNotIn(secret,json.dumps(result))

    def test_metrics_count_tool_rejections_but_not_successful_reads_of_failed_jobs(self):
        def event(status, output, job=None):
            return json.dumps(dict(type='tool_use',part=dict(tool='plc_plc_graph_generate',state=dict(status=status,input=dict(jobId=job) if job else {},output=json.dumps(output)))))
        raw='\n'.join([event('completed',dict(error=dict(code='invalid_graph_ports',message='private details'))),
                       event('error',None),
                       event('completed',dict(job=dict(jobId='failed-job',status='Failed',errorCode='compile_failed'))),
                       event('completed',dict(error=None,value=dict(ok=True))), '{truncated'])
        result=model.observed_metrics(raw)
        self.assertEqual(result['observedToolCalls'],4)
        self.assertEqual(result['invalidToolCalls'],2)
        self.assertEqual(result['failedToolCalls'],1)
        self.assertEqual(result['toolRejectionCodes'],dict(invalid_graph_ports=1))
        self.assertEqual(result['observedJobIds'],['failed-job'])
        self.assertNotIn('private details',json.dumps(result))

    def test_final_narrative_and_connection_are_not_tool_evidence(self):
        with self.assertRaisesRegex(ValueError,'no_observed_mcp_calls'):
            model.calls_from_events(json.dumps(dict(type='text',part=dict(text='Everything compiled successfully.'))),{'plc_job_get'})

    def test_decode_stock_cli_tool_events_and_reject_duplicate_session_or_other_tool(self):
        event=dict(type='tool_use',sessionID='one',part=dict(callID='a',tool='plc_plc_job_get',state=dict(status='completed',input=dict(jobId='job'),output=json.dumps(dict(job=dict(jobId='job'))))))
        self.assertEqual(model.calls_from_events(json.dumps(event),{'plc_job_get'})[0]['output']['job']['jobId'],'job')
        self.assertEqual(model.decode_output(json.dumps([dict(type='text',text=json.dumps(dict(job=dict(jobId='job'))))]))['job']['jobId'],'job')
        with self.assertRaises(ValueError):model.calls_from_events(json.dumps(event)+'\n'+json.dumps(event),{'plc_job_get'})
        event['part']['tool']='bash'
        with self.assertRaisesRegex(ValueError,'unexpected_non_plc_tool'):model.calls_from_events(json.dumps(event),{'plc_job_get'})

    def evidence(self):
        calls=[dict(name='plc_job_get',input=dict(jobId='bad'),output=dict(job=dict(status='Failed',errorCode='compile_failed'))),
               dict(name='plc_project_batch_apply',input=dict(workspaceName='case'))]
        def job(id,op,status='Succeeded',result=None,**kw):return dict(jobId=id,operation=op,status=status,result=result or {},executionMode='fixture',**kw)
        artifact=dict(workspaceName='case',revision='m2:final',executionMode='fixture',artifactId='package')
        jobs=[job('create','project.create'),job('initial','project.batch',result=dict(published=True,revision='m2:initial')),
              job('bad','project.batch','Failed',dict(published=False),errorCode='compile_failed'),
              job('repair','project.batch',result=dict(published=True,revision='m2:final')),
              job('source','pou.read',result=dict(revision='sha',objectId='pou',declaration='PROGRAM P10Main',implementation='Counter := Counter + 2;')),
              job('package','project.package',result=dict(artifact=artifact))]
        head=dict(revision='m2:final',projectPath='/a/project',files=[dict(path='project',sha256='sha')])
        return calls,jobs,head

    def test_proof_requires_server_job_results_diagnostic_observation_and_exact_revision(self):
        calls,jobs,head=self.evidence()
        self.assertEqual(model.prove(calls,jobs,head,'case','fixture')[1],'pou')
        for broken in [jobs[:-1],jobs[:2]+jobs[3:],jobs[:4]+jobs[5:]]:
            with self.assertRaises(ValueError):model.prove(calls,broken,head,'case','fixture')
        with self.assertRaisesRegex(ValueError,'observe_failure'):model.prove(calls[1:],jobs,head,'case','fixture')
        with self.assertRaisesRegex(ValueError,'revision'):model.prove(calls,jobs,dict(head,revision='another'),'case','fixture')

    def test_unknown_status_wrong_mode_and_wrong_workspace_fail(self):
        calls,jobs,head=self.evidence();jobs[2]['status']='OutcomeUnknown'
        with self.assertRaisesRegex(ValueError,'unresolved'):model.prove(calls,jobs,head,'case','fixture')
        calls,jobs,head=self.evidence();jobs[0]['executionMode']='real'
        with self.assertRaisesRegex(ValueError,'mode'):model.prove(calls,jobs,head,'case','fixture')
        calls,jobs,head=self.evidence();calls[1]['input']['workspaceName']='production'
        with self.assertRaisesRegex(ValueError,'scope'):model.prove(calls,jobs,head,'case','fixture')

    def test_provider_environment_is_explicit_and_unrelated_secrets_are_not_passed(self):
        with tempfile.TemporaryDirectory() as temp,patch.dict(os.environ,{'EXAMPLE_API_KEY':'selected','UNRELATED_SECRET':'never','OPENCODE_CONFIG':'private'}):
            env=model.environment(Path(temp),['EXAMPLE_API_KEY'],'mcp',{})
            self.assertEqual(env['EXAMPLE_API_KEY'],'selected');self.assertNotIn('UNRELATED_SECRET',env);self.assertNotIn('OPENCODE_CONFIG',env)
            with self.assertRaises(ValueError):model.environment(Path(temp),['PATH'],'mcp',{})


class GraphicsModelEvidenceTests(unittest.TestCase):
    languages = ('LD', 'FBD', 'SFC')

    def program(self, language, final=False):
        def incoming(source):
            return [dict(name='', connections=[dict(sourceId=source)])]
        output = [dict(name='')]
        if language == 'LD':
            names = ('Enable', 'Running')
            nodes = [dict(id=1, kind='leftPowerRail', outputs=output),
                     dict(id=2, kind='contact', expression='Enable', inputs=incoming(1), outputs=output),
                     dict(id=3, kind='coil', expression='Running', negated=final, inputs=incoming(2), outputs=output),
                     dict(id=4, kind='rightPowerRail', inputs=incoming(3))]
            actions = []
        elif language == 'FBD':
            names = ('Result',)
            nodes = [dict(id=1, kind='inVariable', expression='2' if final else '1', outputs=output),
                     dict(id=2, kind='outVariable', expression='Result', inputs=incoming(1))]
            actions = []
        else:
            names = ('Counter',)
            nodes = [dict(id=1, kind='step', name='Start', initial=True, outputs=output),
                     dict(id=2, kind='transition', expression='TRUE', inputs=incoming(1), outputs=output),
                     dict(id=3, kind='step', name='Run', inputs=incoming(2), outputs=output),
                     dict(id=4, kind='transition', expression='TRUE', inputs=incoming(3), outputs=output),
                     dict(id=5, kind='jumpStep', name='Start', inputs=incoming(4)),
                     dict(id=6, kind='actionBlock', inputs=incoming(3),
                          associations=[dict(id=1, action='Mark', qualifier='N')])]
            actions = [dict(name='Mark', implementation='Counter := Counter + '+('2' if final else '1')+';')]
        return dict(name='Model'+language, language=language, profile='graph-v2', pouType='program',
                    variables=[dict(name=name, type='BOOL' if language == 'LD' else 'DINT',
                                    scope='local', documentation='model regression') for name in names],
                    variableGroups=[dict(name=language+'Vars', scope='local', variables=list(names))],
                    nodes=nodes, actions=actions)

    @staticmethod
    def semantic_hash(program):
        # Opaque deterministic fixture digests; the server computes real graph digests.
        return hashlib.sha256(json.dumps(program, sort_keys=True).encode()).hexdigest()

    def evidence(self):
        def job(identity, operation, result):
            return dict(jobId=identity, operation=operation, status='Succeeded', executionMode='fixture', result=result)
        def exchange(program, object_id):
            return dict(objectIds=[object_id], graph=dict(program=copy.deepcopy(program), semanticSha256=self.semantic_hash(program)))
        def submit(name, record, **arguments):
            return dict(name=name, input=dict(workspaceName='case', **arguments), status='completed',
                        output=dict(job={k: record[k] for k in ('jobId', 'operation')} | dict(status='Queued')))
        jobs = [job('create', 'project.create', {})]
        calls = []
        for language in self.languages:
            first = exchange(self.program(language), language+'-first-object')
            final = exchange(self.program(language, final=True), language+'-final-object')
            final['report'] = [dict(name='Model'+language, oldObjectId=first['objectIds'][0], objectId=final['objectIds'][0])]
            first_write = job(language+'-write-first', 'pou.graph.write', dict(published=True, revision='m2:'+language+'-first', engineeringResult=dict(exchange=first)))
            first_read = job(language+'-read-first', 'pou.graph.read', dict(revision=language+'-first-file', exchange=copy.deepcopy(first)))
            final_write = job(language+'-write-final', 'pou.graph.write', dict(published=True, revision='m2:'+language+'-final', engineeringResult=dict(exchange=final)))
            final_read = job(language+'-read-final', 'pou.graph.read', dict(revision='final-file-sha', exchange=copy.deepcopy(final)))
            jobs.extend((first_write, first_read, final_write, final_read))
            calls.extend((submit('plc_pou_graph_write', first_write),
                          submit('plc_pou_graph_read', first_read, objectId=first['objectIds'][0]),
                          dict(name='plc_job_get', input=dict(jobId=first_read['jobId']), status='completed', output=dict(job=copy.deepcopy(first_read))),
                          submit('plc_pou_graph_write', final_write, objectId=first['objectIds'][0], expectedGraphSha256=first['graph']['semanticSha256']),
                          submit('plc_pou_graph_read', final_read, objectId=final['objectIds'][0]),
                          dict(name='plc_job_get', input=dict(jobId=final_read['jobId']), status='completed', output=dict(job=copy.deepcopy(final_read)))))
        jobs.append(job('package', 'project.package', dict(artifact=dict(workspaceName='case', revision='m2:final', executionMode='fixture', artifactId='zip'))))
        head = dict(projectPath='/case/project', files=[dict(path='project', sha256='final-file-sha')], revision='m2:final')
        return calls, jobs, head

    @staticmethod
    def record(jobs, language, stage):
        return next(j for j in jobs if j['jobId'] == language+'-'+stage)

    @staticmethod
    def exchange(job):
        result = job['result']
        return result.get('engineeringResult', result)['exchange']

    @staticmethod
    def observed(calls, language):
        return next(c for c in calls if c['name'] == 'plc_job_get' and c['input']['jobId'] == language+'-read-first')

    @staticmethod
    def write_call(calls, language, stage='final'):
        return next(c for c in calls if c['name'] == 'plc_pou_graph_write' and c['output']['job']['jobId'] == language+'-write-'+stage)

    @staticmethod
    def prove(calls, jobs, head):
        import model_scenarios
        return model_scenarios.prove('graphics', calls, jobs, head, 'case', 'fixture', {})

    def test_initial_read_then_real_modification_and_final_readback_pass(self):
        self.assertEqual('zip', self.prove(*self.evidence())['artifactId'])

    def test_successful_get_result_and_direct_terminal_read_are_supported(self):
        for wrapper in ('plc_job_get_result', 'plc_pou_graph_read'):
            with self.subTest(wrapper=wrapper):
                calls, jobs, head = self.evidence()
                for language in self.languages:
                    observed = self.observed(calls, language)
                    if wrapper == 'plc_job_get_result': observed['name'] = wrapper
                    else:
                        submitted = next(c for c in calls if c['name'] == wrapper and c['output']['job']['jobId'] == language+'-read-first')
                        submitted['output'] = copy.deepcopy(observed['output'])
                        calls.remove(observed)
                self.assertEqual('zip', self.prove(calls, jobs, head)['artifactId'])

    def test_implicit_false_node_renumbering_and_retained_object_ids_are_supported(self):
        for change in ('implicit_false', 'renumbered_nodes', 'retained_object_ids'):
            with self.subTest(change=change):
                calls, jobs, head = self.evidence()
                if change == 'implicit_false':
                    first = self.exchange(self.record(jobs, 'LD', 'write-first'))
                    first['graph']['program']['nodes'][2].pop('negated')
                    first['graph']['semanticSha256'] = self.semantic_hash(first['graph']['program'])
                    self.record(jobs, 'LD', 'read-first')['result']['exchange'] = copy.deepcopy(first)
                    self.observed(calls, 'LD')['output']['job'] = copy.deepcopy(self.record(jobs, 'LD', 'read-first'))
                    self.write_call(calls, 'LD')['input']['expectedGraphSha256'] = first['graph']['semanticSha256']
                else:
                    for language in self.languages:
                        final = self.exchange(self.record(jobs, language, 'write-final'))
                        if change == 'renumbered_nodes':
                            for node in final['graph']['program']['nodes']:
                                node['id'] += 100
                                for port in node.get('inputs', []):
                                    for connection in port.get('connections', []): connection['sourceId'] += 100
                            final['graph']['semanticSha256'] = self.semantic_hash(final['graph']['program'])
                        else:
                            final['objectIds'] = [language+'-first-object']
                            final.pop('report')
                        self.record(jobs, language, 'read-final')['result']['exchange'] = copy.deepcopy(final)
                self.assertEqual('zip', self.prove(calls, jobs, head)['artifactId'])

    def test_final_state_created_first_then_metadata_changed_is_not_programming_evidence(self):
        for language in self.languages:
            with self.subTest(language=language):
                calls, jobs, head = self.evidence()
                first = self.exchange(self.record(jobs, language, 'write-first'))
                first['graph']['program'] = self.program(language, final=True)
                first['graph']['program']['variableGroups'][0]['name'] = 'InitialMetadata'
                first['graph']['semanticSha256'] = self.semantic_hash(first['graph']['program'])
                self.record(jobs, language, 'read-first')['result']['exchange'] = copy.deepcopy(first)
                self.observed(calls, language)['output']['job'] = copy.deepcopy(self.record(jobs, language, 'read-first'))
                self.write_call(calls, language)['input']['expectedGraphSha256'] = first['graph']['semanticSha256']
                with self.assertRaisesRegex(ValueError, 'initial_state_missing'): self.prove(calls, jobs, head)

    def test_each_language_requires_requested_final_behavior(self):
        for language in self.languages:
            with self.subTest(language=language):
                calls, jobs, head = self.evidence()
                for stage in ('write-final', 'read-final'):
                    graph = self.exchange(self.record(jobs, language, stage))['graph']
                    graph['program'] = self.program(language)
                    graph['program']['variableGroups'][0]['name'] = 'OnlyMetadataChanged'
                    graph['semanticSha256'] = self.semantic_hash(graph['program'])
                with self.assertRaisesRegex(ValueError, language.lower()+'_modification_missing'): self.prove(calls, jobs, head)

    def test_correct_values_on_unrelated_nodes_or_actions_are_rejected(self):
        for final in (False, True):
            for language in self.languages:
                with self.subTest(final=final, language=language):
                    calls, jobs, head = self.evidence()
                    for stage in (('write-final', 'read-final') if final else ('write-first', 'read-first')):
                        program = self.exchange(self.record(jobs, language, stage))['graph']['program']
                        if language == 'LD': program['nodes'][2]['expression'] = 'UnrelatedCoil'
                        elif language == 'FBD': program['nodes'][1]['inputs'][0]['connections'][0]['sourceId'] = 999
                        else: program['nodes'][-1]['inputs'][0]['connections'][0]['sourceId'] = 1  # Start, not Run.
                    error = language.lower()+'_modification_missing' if final else 'initial_state_missing'
                    with self.assertRaisesRegex(ValueError, error): self.prove(calls, jobs, head)

    def test_initial_read_must_be_observed_before_final_write_for_each_language(self):
        for language in self.languages:
            for change in ('missing', 'late', 'pending', 'error', 'wrong_input_job', 'wrong_output_job'):
                with self.subTest(language=language, change=change):
                    calls, jobs, head = self.evidence()
                    observed = self.observed(calls, language)
                    if change == 'missing': calls.remove(observed)
                    elif change == 'late': calls.remove(observed); calls.append(observed)
                    elif change == 'pending': observed['output']['job'].update(status='Running', result=None)
                    elif change == 'error': observed.update(status='error')
                    elif change == 'wrong_input_job': observed['input']['jobId'] = 'unrelated-read'
                    else: observed['output']['job']['jobId'] = 'unrelated-read'
                    with self.assertRaisesRegex(ValueError, 'initial_read_not_observed_before_modification'): self.prove(calls, jobs, head)

    def test_initial_read_server_and_observed_result_must_match_first_write(self):
        for location in ('server', 'observed'):
            for change in ('object', 'hash', 'name', 'operation', 'value'):
                with self.subTest(location=location, change=change):
                    calls, jobs, head = self.evidence()
                    record = self.record(jobs, 'LD', 'read-first') if location == 'server' else self.observed(calls, 'LD')['output']['job']
                    exchange = self.exchange(record)
                    if change == 'object': exchange['objectIds'] = ['unrelated-object']
                    elif change == 'hash': exchange['graph']['semanticSha256'] = 'unrelated-hash'
                    elif change == 'name': exchange['graph']['program']['name'] = 'UnrelatedProgram'
                    elif change == 'operation': record['operation'] = 'pou.graph.write'
                    else: exchange['graph']['program']['nodes'][2]['negated'] = True
                    with self.assertRaisesRegex(ValueError, 'initial_read_not_observed_before_modification'): self.prove(calls, jobs, head)

    def test_disconnected_success_and_hash_fields_do_not_prove_a_read(self):
        calls, jobs, head = self.evidence()
        observed = self.observed(calls, 'LD')
        observed['output'] = dict(job=dict(jobId='LD-read-first', operation='pou.graph.read', status='Running'),
                                  unrelated=dict(status='Succeeded', result=copy.deepcopy(self.record(jobs, 'LD', 'read-first')['result'])))
        with self.assertRaisesRegex(ValueError, 'initial_read_not_observed_before_modification'): self.prove(calls, jobs, head)

    def test_published_writes_must_be_observed_as_submissions(self):
        for stage in ('first', 'final'):
            for change in ('no_call', 'unrelated_job', 'unpublished', 'wrong_operation', 'server_wrong_operation', 'call_error'):
                with self.subTest(stage=stage, change=change):
                    calls, jobs, head = self.evidence()
                    submitted = self.write_call(calls, 'LD', stage)
                    record = self.record(jobs, 'LD', 'write-'+stage)
                    if change == 'no_call': calls.remove(submitted)
                    elif change == 'unrelated_job': submitted['output']['job']['jobId'] = 'unrelated-write'
                    elif change == 'unpublished': record['result']['published'] = False
                    elif change == 'wrong_operation': submitted['output']['job']['operation'] = 'pou.graph.read'
                    elif change == 'server_wrong_operation': record['operation'] = 'pou.graph.read'
                    else: submitted['status'] = 'error'
                    with self.assertRaises(ValueError): self.prove(calls, jobs, head)

    def test_replacement_is_bound_to_initial_object_hash_and_replacement_report(self):
        for change in ('object', 'expected_hash', 'old_object', 'new_object', 'report_name', 'missing_report'):
            with self.subTest(change=change):
                calls, jobs, head = self.evidence()
                submitted = self.write_call(calls, 'LD')
                final = self.exchange(self.record(jobs, 'LD', 'write-final'))
                if change == 'object': submitted['input']['objectId'] = 'unrelated-object'
                elif change == 'expected_hash': submitted['input']['expectedGraphSha256'] = 'unobserved-hash'
                elif change == 'old_object': final['report'][0]['oldObjectId'] = 'unrelated-object'
                elif change == 'new_object': final['report'][0]['objectId'] = 'unrelated-object'
                elif change == 'report_name': final['report'][0]['name'] = 'UnrelatedProgram'
                else: final.pop('report')
                with self.assertRaisesRegex(ValueError, 'modification_evidence_missing'): self.prove(calls, jobs, head)

    def test_final_read_is_bound_to_last_written_graph_and_object(self):
        for language in self.languages:
            for change in ('read_hash', 'write_hash', 'same_hash', 'object', 'write_name', 'read_name'):
                with self.subTest(language=language, change=change):
                    calls, jobs, head = self.evidence()
                    final = self.exchange(self.record(jobs, language, 'write-final'))
                    read = self.exchange(self.record(jobs, language, 'read-final'))
                    if change == 'read_hash': read['graph']['semanticSha256'] = 'unwritten-hash'
                    elif change == 'write_hash': final['graph']['semanticSha256'] = 'not-read-back'
                    elif change == 'same_hash':
                        digest = self.exchange(self.record(jobs, language, 'write-first'))['graph']['semanticSha256']
                        final['graph']['semanticSha256'] = read['graph']['semanticSha256'] = digest
                    elif change == 'object': read['objectIds'] = ['unrelated-object']
                    elif change == 'write_name': final['graph']['program']['name'] = 'UnrelatedProgram'
                    else: read['graph']['program']['name'] = 'UnrelatedProgram'
                    with self.assertRaises(ValueError): self.prove(calls, jobs, head)

    def test_final_file_head_and_exact_delivery_guards_remain_required(self):
        for change, error in (('stale_read', 'final_graph_readback_missing'), ('head_revision', 'exact_final_delivery_missing'),
                              ('package_revision', 'exact_final_delivery_missing'), ('package_workspace', 'exact_final_delivery_missing'),
                              ('package_mode', 'exact_final_delivery_missing'), ('missing_package', 'exact_final_delivery_missing')):
            with self.subTest(change=change):
                calls, jobs, head = self.evidence()
                artifact = jobs[-1]['result']['artifact']
                if change == 'stale_read': self.record(jobs, 'LD', 'read-final')['result']['revision'] = 'previous-file-sha'
                elif change == 'head_revision': head['revision'] = 'm2:later'
                elif change == 'package_revision': artifact['revision'] = 'm2:earlier'
                elif change == 'package_workspace': artifact['workspaceName'] = 'unrelated-workspace'
                elif change == 'package_mode': artifact['executionMode'] = 'real'
                else: jobs.pop()
                with self.assertRaisesRegex(ValueError, error): self.prove(calls, jobs, head)

    def test_graph_workspace_mode_and_unresolved_jobs_remain_rejected(self):
        for change, error in (('workspace', 'workspace_scope_mismatch'), ('mode', 'job_mode_mismatch'), ('unknown', 'job_outcome_unresolved')):
            with self.subTest(change=change):
                calls, jobs, head = self.evidence()
                if change == 'workspace': calls[0]['input']['workspaceName'] = 'production'
                elif change == 'mode': jobs[0]['executionMode'] = 'real'
                else: self.record(jobs, 'LD', 'write-final')['status'] = 'OutcomeUnknown'
                with self.assertRaisesRegex(ValueError, error): self.prove(calls, jobs, head)

    def test_final_variable_groups_and_documentation_remain_required(self):
        for field in ('variableGroups', 'documentation'):
            with self.subTest(field=field):
                calls, jobs, head = self.evidence()
                program = self.exchange(self.record(jobs, 'LD', 'read-final'))['graph']['program']
                if field == 'variableGroups': program[field] = []
                else: program['variables'][0][field] = 'unrequested'
                final = self.exchange(self.record(jobs, 'LD', 'write-final'))['graph']
                final['program'] = copy.deepcopy(program)
                final['semanticSha256'] = self.semantic_hash(program)
                self.exchange(self.record(jobs, 'LD', 'read-final'))['graph']['semanticSha256'] = final['semanticSha256']
                with self.assertRaisesRegex(ValueError, 'graph_declaration_readback_missing'): self.prove(calls, jobs, head)
