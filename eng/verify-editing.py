"""HTTP integration checks against a dedicated fixture host, never a PLC IDE."""
from pathlib import Path
import argparse
import json
import os
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument('--dotnet', default='dotnet')
args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
host = root / 'tests/PlcMcp.TestHost/bin/Release/net8.0/PlcMcp.TestHost.dll'
BASE = 'http://127.0.0.1:5088'
try:
    urllib.request.urlopen(BASE + '/health/live', timeout=1).close()
except OSError:
    pass
else:
    raise RuntimeError('Port 5088 is occupied; this test will not stop another host.')


class Client:
    def __init__(self, key):
        self.key, self.next_id, self.protocol = key, 0, '2025-11-25'
        self.rpc('initialize', dict(protocolVersion=self.protocol, capabilities={}, clientInfo=dict(name='plcmcp-fixture-check', version='1.0')))
    def rpc(self, method, params=None):
        self.next_id += 1
        payload = dict(jsonrpc='2.0', id=self.next_id, method=method)
        if params is not None: payload['params'] = params
        headers = {'Authorization': 'Bearer ' + self.key, 'Content-Type': 'application/json',
                   'Accept': 'application/json, text/event-stream', 'MCP-Protocol-Version': self.protocol}
        with urllib.request.urlopen(urllib.request.Request(BASE + '/mcp', json.dumps(payload).encode(), headers), timeout=20) as response:
            text = response.read().decode()
            result = [json.loads(line[5:].strip()) for line in text.splitlines() if line.startswith('data:')][-1] if 'text/event-stream' in response.headers.get('Content-Type', '') else json.loads(text)
        assert 'error' not in result, result
        if method == 'initialize': self.protocol = result['result']['protocolVersion']
        return result['result']
    def call(self, name, arguments, error=False):
        result = self.rpc('tools/call', dict(name=name, arguments=arguments))
        assert bool(result.get('isError', False)) == error, result
        value = result.get('structuredContent')
        if value is None: value = json.loads(result['content'][0]['text'])
        if name not in ('plc_capabilities_list',):
            assert value['executionMode'] == 'fixture', value
            assert value['evidenceKind'] == 'simulated', value
            assert value['engineeringReady'] is False, value
        return value
    def wait(self, job_id, wanted=None):
        until = time.monotonic() + 25
        while time.monotonic() < until:
            job = self.call('plc_job_get', dict(jobId=job_id))['job']
            if wanted and job['status'] == wanted: return job
            if not wanted and job['status'] not in ('Queued', 'Running', 'CancelRequested'): return job
            time.sleep(0.05)
        raise AssertionError('Job did not reach expected state')


def stop(process):
    if process.poll() is None:
        process.terminate()
        try: process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill(); process.wait(timeout=5)


with tempfile.TemporaryDirectory(prefix='plcmcp-edit-check-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
    directory = Path(tmp)
    projects = directory / '产线项目'; projects.mkdir()
    template = projects / 'templates' / 'sample'; template.mkdir(parents=True)
    original = json.loads((root / 'tests/fixtures/sample.fixture.json').read_text())
    original.update(compileError=False)
    project_file = template / 'project.fixture.json'
    project_file.write_text(json.dumps(original), encoding='utf-8')
    original_bytes = project_file.read_bytes()
    definition = dict(id='sample', projectFile=project_file.name, files=[project_file.name], ideProfile='fixture',
                      targetController='fixture-controller', requiredLibraries=[], executionMode='fixture')
    (template / 'template.json').write_text(json.dumps(definition), encoding='utf-8')
    env = dict(os.environ, PlcMcp__ApiKey=secrets.token_hex(32), DOTNET_ENVIRONMENT='Development',
               PlcMcp__DataDirectory=str(directory / 'data'), PlcMcp__WorkspaceRoot=str(projects),
               DOTNET_HOST_PATH=str(Path(shutil.which(args.dotnet) or args.dotnet).resolve()))
    process = None
    def start():
        process = subprocess.Popen([args.dotnet, str(host)], env=env, cwd=host.parent, stdout=log, stderr=log)
        for _ in range(300):
            if process.poll() is not None: raise RuntimeError('Fixture host exited')
            try:
                urllib.request.urlopen(BASE + '/health/live', timeout=1).close()
                return process
            except OSError: time.sleep(0.1)
        stop(process)
        raise RuntimeError('Fixture host readiness timeout')
    try:
        process = start(); client = Client(env['PlcMcp__ApiKey'])
        catalog = client.call('plc_templates_list', {})['value']
        assert len(catalog) == 1 and catalog[0]['errorCode'] is None, catalog
        create_args = dict(workspaceName='demo', templateId='sample', idempotencyKey='create', expectedTemplateRevision=catalog[0]['revision'])
        create = client.call('plc_project_create', create_args)['job']
        created = client.wait(create['jobId']); assert created['status'] == 'Succeeded', created
        assert client.call('plc_project_create', create_args)['job']['jobId'] == create['jobId']
        initial = client.call('plc_workspace_get', dict(workspaceName='demo'))['value']
        # Model clients often consume MCP text rather than structuredContent.
        # Unicode paths must remain readable there without changing the JSON value.
        wire = client.rpc('tools/call', dict(name='plc_workspace_get', arguments=dict(workspaceName='demo')))
        text_json = wire['content'][0]['text']
        assert '产线项目' in text_json and json.loads(text_json) == wire['structuredContent']
        assert Path(json.loads(text_json)['value']['projectPath']).is_file()
        revision = initial['revision']
        def edit(tool_name, **fields):
            global revision
            args = dict(workspaceName='demo', expectedRevision=revision, idempotencyKey=secrets.token_hex(12), **fields)
            receipt = client.call(tool_name, args)['job']
            finished = client.wait(receipt['jobId'])
            assert finished['status'] == 'Succeeded', finished
            value = client.call('plc_job_get_result', dict(jobId=receipt['jobId']))['job']['result']
            assert value['published'] and value['simulated']
            assert value['revisionScope'] == 'managed_project_manifest_v2'
            assert value['baseRevision'] == revision
            assert Path(value['projectPath']).is_file() and Path(value['backupPath']).is_dir()
            revision = value['revision']
            return args, receipt, value
        pou_args, pou_job, pou = edit('plc_pou_create', parentId=original['applicationId'], name='Counter', pouType='FunctionBlock', language='ST',
                                      declaration='FUNCTION_BLOCK Counter\nVAR\nvalue : INT;\nEND_VAR', implementation='(* FIXTURE_ERROR *)\nvalue := value + 1;')
        object_id = pou['engineeringResult']['objectId']
        tree = client.call('plc_project_get_structure', dict(projectPath=pou['projectPath'], idempotencyKey='tree'))['job']
        assert any(n['id'] == object_id for n in client.wait(tree['jobId'])['result']['objects'])
        compile_args = dict(projectPath=pou['projectPath'], applicationId=original['applicationId'], idempotencyKey='compile-fail')
        compilation = client.call('plc_project_compile', compile_args)['job']
        failed = client.wait(compilation['jobId']); assert failed['status'] == 'Failed', failed
        assert failed['result']['diagnostics'][0]['objectId'] == object_id
        assert client.call('plc_job_get_result', dict(jobId=compilation['jobId']), error=True)['error']['code'] == 'compile_failed'
        before_edit = revision
        update_args, update_job, fixed = edit('plc_pou_update', objectId=object_id, implementation='// 计数测试\nvalue := value + 1;')
        assert client.call('plc_pou_update', update_args)['job']['jobId'] == update_job['jobId']
        conflict = client.call('plc_pou_update', dict(update_args, idempotencyKey='stale', implementation='wrong'), error=True)
        assert conflict['error']['code'] == 'workspace_revision_conflict'
        conflict = client.call('plc_pou_update', dict(update_args, implementation='different request'), error=True)
        assert conflict['error']['code'] == 'idempotency_conflict'
        read = client.call('plc_pou_read', dict(projectPath=fixed['projectPath'], objectId=object_id, idempotencyKey='read'))['job']
        assert client.wait(read['jobId'])['result']['implementation'] == '// 计数测试\nvalue := value + 1;'
        compiled = client.call('plc_project_compile', dict(compile_args, projectPath=fixed['projectPath'], idempotencyKey='compile-fixed'))['job']
        assert client.wait(compiled['jobId'])['status'] == 'Succeeded'
        _, _, gvl = edit('plc_gvl_create', parentId=original['applicationId'], name='Globals', declaration='VAR_GLOBAL\nrun : BOOL;\nEND_VAR')
        read_gvl = client.call('plc_gvl_read', dict(projectPath=gvl['projectPath'], objectId=gvl['engineeringResult']['objectId'], idempotencyKey='read-gvl'))['job']
        assert client.wait(read_gvl['jobId'])['result']['declaration'].startswith('VAR_GLOBAL')
        edit('plc_gvl_update', objectId=gvl['engineeringResult']['objectId'], declaration='VAR_GLOBAL\nrun : BOOL;\ncount : INT;\nEND_VAR')
        _, _, dut = edit('plc_dut_create', parentId=original['applicationId'], name='State', dutType='Enumeration', declaration='TYPE State : (Idle, Running); END_TYPE')
        read_dut = client.call('plc_dut_read', dict(projectPath=dut['projectPath'], objectId=dut['engineeringResult']['objectId'], idempotencyKey='read-dut'))['job']
        assert client.wait(read_dut['jobId'])['result']['declaration'].startswith('TYPE State')
        edit('plc_dut_update', objectId=dut['engineeringResult']['objectId'], declaration='TYPE State : (Idle, Running, Stopped); END_TYPE')
        _, saved_job, saved = edit('plc_project_save')
        assert saved['isDelivery'] is True
        assert project_file.read_bytes() == original_bytes
        assert Path(initial['projectPath']).is_file()
        assert client.call('plc_workspace_get', dict(workspaceName='demo'))['value']['revision'] == revision
        stop(process); process = start(); client = Client(env['PlcMcp__ApiKey'])
        assert client.call('plc_workspace_get', dict(workspaceName='demo'))['value']['revision'] == revision
        assert client.call('plc_job_reconcile', dict(jobId=saved_job['jobId']))['job']['status'] == 'Succeeded'
        for template_id, fault, outcome in [('bad-save','save_failure','Failed'), ('unknown-save','after_save_exit','OutcomeUnknown')]:
            folder = projects / 'templates' / template_id; folder.mkdir()
            (folder / project_file.name).write_text(json.dumps(dict(original, fault=fault)), encoding='utf-8')
            (folder / 'template.json').write_text(json.dumps(dict(definition, id=template_id)), encoding='utf-8')
            job = client.call('plc_project_create', dict(workspaceName=template_id, templateId=template_id, idempotencyKey='fault'))['job']
            result = client.wait(job['jobId']); assert result['status'] == outcome, result
            assert result['result']['published'] is False
            assert client.call('plc_workspace_get', dict(workspaceName=template_id), error=True)['error']['code'] == 'workspace_not_found'
            if outcome == 'OutcomeUnknown':
                assert client.call('plc_job_reconcile', dict(jobId=job['jobId']), error=True)['error']['code'] == 'reconciliation_unproven'
                assert client.call('plc_runtime_get_status', {})['schedulerBlocked'] is True
        print('PASS: template creation, POU/GVL/DUT writes, simulated diagnostic repair, version conflicts, source preservation, delivery, restart and failed/unknown saves')
    except Exception:
        log.seek(0); print(log.read())
        raise
    finally:
        if process is not None: stop(process)
