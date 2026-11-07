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

    def test_each_graph_language_requires_two_distinct_written_semantics_and_final_readback(self):
        import copy
        import model_scenarios
        programs = [dict(name='ModelLD', nodes=[dict(kind='coil', negated=True)]),
                    dict(name='ModelFBD', nodes=[dict(kind='inVariable', expression='2')]),
                    dict(name='ModelSFC', nodes=[], actions=[dict(name='Mark', implementation='Counter := Counter + 2;')])]
        for p in programs:
            p.update(variableGroups=[dict(scope='local', variables=['x'])], variables=[dict(name='x', documentation='model regression')])
        jobs = [dict(status='Succeeded', executionMode='fixture', operation='project.create', result={})]
        for p in programs:
            for number in (1, 2):
                jobs.append(dict(status='Succeeded', executionMode='fixture', operation='pou.graph.write',
                                 result=dict(exchange=dict(graph=dict(program=p, semanticSha256=p['name']+str(number))))))
            jobs.append(dict(status='Succeeded', executionMode='fixture', operation='pou.graph.read',
                             result=dict(revision='sha', exchange=dict(graph=dict(program=p)))))
        artifact=dict(workspaceName='case', revision='m2:final', executionMode='fixture', artifactId='zip')
        jobs.append(dict(status='Succeeded', executionMode='fixture', operation='project.package', result=dict(artifact=artifact)))
        head=dict(projectPath='/case/project', files=[dict(path='project', sha256='sha')], revision='m2:final')
        self.assertEqual(model_scenarios.prove('graphics', [], jobs, head, 'case', 'fixture', {})['artifactId'], 'zip')
        broken = copy.deepcopy(jobs)
        for j in broken:
            if j['operation'] == 'pou.graph.write' and j['result']['exchange']['graph']['program']['name'] == 'ModelSFC':
                j['result']['exchange']['graph']['semanticSha256'] = 'unchanged'
        with self.assertRaisesRegex(ValueError, 'modification_missing'):
            model_scenarios.prove('graphics', [], broken, head, 'case', 'fixture', {})
        broken = copy.deepcopy(jobs)
        for j in broken:
            if j['operation'] == 'pou.graph.read': j['result']['revision'] = 'old-file-hash'
        with self.assertRaisesRegex(ValueError, 'readback_missing'):
            model_scenarios.prove('graphics', [], broken, head, 'case', 'fixture', {})

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
