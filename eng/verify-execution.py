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
parser.add_argument('--write-contracts', action='store_true')
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


with tempfile.TemporaryDirectory(prefix='plcmcp-fixture-check-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
    directory = Path(tmp)
    projects = directory / 'projects'; projects.mkdir()
    sample = projects / 'sample.fixture.json'
    fixture = json.loads((root / 'tests/fixtures/sample.fixture.json').read_text())
    sample.write_text(json.dumps(fixture), encoding='utf-8')
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
        tools = client.rpc('tools/list')['tools']
        names = {tool['name'] for tool in tools}
        expected = {'plc_capabilities_list', 'plc_runtime_get_status', 'plc_project_open', 'plc_project_get_structure',
                    'plc_pou_read', 'plc_project_compile', 'plc_job_get', 'plc_job_cancel', 'plc_job_get_result', 'plc_templates_list', 'plc_workspace_get',
                    'plc_project_create', 'plc_project_save', 'plc_pou_create', 'plc_pou_update',
                    'plc_gvl_create', 'plc_gvl_update', 'plc_dut_create', 'plc_dut_update', 'plc_job_reconcile', 'plc_gvl_read', 'plc_dut_read'}
        expected.update({'plc_object_inspect', 'plc_device_set_parameter', 'plc_task_set_calls', 'plc_pou_member_create', 'plc_device_set_io_mapping', 'plc_folder_create', 'plc_library_remove', 'plc_pou_member_update', 'plc_task_create', 'plc_task_configuration_create', 'plc_task_update', 'plc_device_get_parameters', 'plc_device_add', 'plc_object_delete', 'plc_library_list_installed', 'plc_library_add', 'plc_object_rename', 'plc_object_move', 'plc_task_read', 'plc_task_list', 'plc_device_remove', 'plc_library_set_placeholder', 'plc_pou_member_read', 'plc_device_get_tree', 'plc_library_list', 'plc_task_delete', 'plc_device_get_io_mappings'})
        expected.update({'plc_exchange_stage', 'plc_exchange_read', 'plc_exchange_validate', 'plc_graph_generate', 'plc_graph_decode', 'plc_project_export', 'plc_project_import', 'plc_pou_graph_read', 'plc_pou_graph_write'})
        expected.update({'plc_project_batch_preview', 'plc_project_batch_apply', 'plc_batch_get', 'plc_exchange_upload_begin', 'plc_exchange_upload_append', 'plc_exchange_upload_complete', 'plc_exchange_upload_abort', 'plc_delivery_create', 'plc_delivery_read'})
        expected.update({'plc_project_adopt_preview', 'plc_project_adopt', 'plc_project_context', 'plc_project_history', 'plc_project_diff', 'plc_project_restore'})
        assert names == expected, names
        for tool in tools:
            assert tool['inputSchema']['type'] == 'object'
            assert tool['outputSchema']['type'] == 'object', tool
        if args.write_contracts:
            target = root / 'contracts/mcp-tools.v1.json'; target.parent.mkdir(exist_ok=True)
            target.write_text(json.dumps(sorted(tools, key=lambda t:t['name']), indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        else:
            expected_tools = json.loads((root / 'contracts/mcp-tools.v1.json').read_text(encoding='utf-8'))
            assert sorted(tools, key=lambda t:t['name']) == expected_tools, 'MCP contract snapshot changed; review and update explicitly'
        program_schema = json.loads(json.dumps(next(t for t in tools if t['name'] == 'plc_graph_generate')['inputSchema']['properties']['program'])
                                    .replace('"#/properties/program/', '"#/'))
        graph_schema = {'$schema': 'https://json-schema.org/draft/2020-12/schema', '$id': 'urn:plcmcp:graph-v1',
                        'title': 'Graph-v1 transport shape',
                        'description': 'Transport shape only. Runtime GraphRules and PLCopen codec additionally enforce profile, topology, limits and loss checks.', **program_schema}
        graph_target = root / 'contracts/graph-v1.schema.json'
        if args.write_contracts:
            graph_target.write_text(json.dumps(graph_schema, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        else:
            assert json.loads(graph_target.read_text(encoding='utf-8')) == graph_schema, 'Graph contract snapshot changed'
        plan_schema = json.loads(json.dumps(next(t for t in tools if t['name'] == 'plc_project_batch_preview')['inputSchema']['properties']['plan'])
                                 .replace('"#/properties/plan/', '"#/'))
        batch_schema = {'$schema': 'https://json-schema.org/draft/2020-12/schema', '$id': 'urn:plcmcp:batch-v1',
                        'title': 'Batch-v1 transport shape',
                        'description': 'Runtime validation additionally enforces 32 steps, operation fields, supplied dependency order, @stepId references and Application scope.', **plan_schema}
        batch_target = root / 'contracts/batch-v1.schema.json'
        if args.write_contracts:
            batch_target.write_text(json.dumps(batch_schema, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
        else:
            assert json.loads(batch_target.read_text(encoding='utf-8')) == batch_schema, 'Batch contract snapshot changed'
        assert not client.call('plc_runtime_get_status', {})['schedulerBlocked']
        assert all(c['status'] == 'Unverified' for c in client.call('plc_capabilities_list', {})['capabilities'])
        common = dict(projectPath=str(sample), idempotencyKey='open-1')
        receipt = client.call('plc_project_open', common)['job']
        assert client.wait(receipt['jobId'])['status'] == 'Succeeded'
        assert client.call('plc_project_open', common)['job']['jobId'] == receipt['jobId']
        assert client.call('plc_project_open', dict(common, timeoutSeconds=61), error=True)['error']['code'] == 'idempotency_conflict'
        structure = client.call('plc_project_get_structure', dict(common, idempotencyKey='tree', maxObjects=1))['job']
        assert client.wait(structure['jobId'])['result']['truncated'] is True
        source = client.call('plc_pou_read', dict(common, idempotencyKey='read', objectId=fixture['programId']))['job']
        assert client.wait(source['jobId'])['result']['implementation'] == fixture['implementation']
        compilation = client.call('plc_project_compile', dict(common, idempotencyKey='compile', applicationId=fixture['applicationId']))['job']
        assert client.wait(compilation['jobId'])['status'] == 'Failed'
        result = client.call('plc_job_get_result', dict(jobId=compilation['jobId']), error=True)
        assert result['error']['code'] == 'compile_failed' and result['job']['result']['diagnostics']
        assert client.call('plc_pou_read', dict(common, objectId='not-a-guid'), error=True)['error']['code'] == 'invalid_request'
        assert client.call('plc_job_get', dict(jobId='00000000-0000-0000-0000-000000000001'), error=True)['error']['code'] == 'job_not_found'
        stop(process); process = start(); client = Client(env['PlcMcp__ApiKey'])
        assert client.call('plc_job_get_result', dict(jobId=receipt['jobId']))['job']['status'] == 'Succeeded'
        slow = projects / 'slow.fixture.json'; slow.write_text(json.dumps(dict(fixture, delayMilliseconds=5000)), encoding='utf-8')
        running = client.call('plc_project_open', dict(projectPath=str(slow), idempotencyKey='running'))['job']
        client.wait(running['jobId'], 'Running')
        queued = client.call('plc_project_open', dict(projectPath=str(slow), idempotencyKey='queued'))['job']
        assert client.call('plc_job_get_result', dict(jobId=queued['jobId']), error=True)['error']['code'] == 'job_not_finished'
        assert client.call('plc_job_cancel', dict(jobId=queued['jobId']))['job']['status'] == 'Cancelled'
        client.call('plc_job_cancel', dict(jobId=running['jobId']))
        assert client.wait(running['jobId'])['status'] == 'Cancelled'
        timeout = client.call('plc_project_open', dict(projectPath=str(slow), idempotencyKey='timeout', timeoutSeconds=1))['job']
        assert client.wait(timeout['jobId'])['status'] == 'OutcomeUnknown'
        assert client.call('plc_runtime_get_status', {})['schedulerBlocked'] is True
        assert client.call('plc_project_open', dict(common, idempotencyKey='blocked'), error=True)['error']['code'] == 'execution_outcome_unknown'
        stop(process); process = start(); client = Client(env['PlcMcp__ApiKey'])
        assert client.call('plc_runtime_get_status', {})['schedulerBlocked'] is True
        print('PASS: sixty-seven tool schemas, fixture evidence, persistent jobs, source, diagnostics, idempotency, cancellation, timeout and restart quarantine')
    except Exception:
        log.seek(0); print(log.read())
        raise
    finally:
        if process is not None: stop(process)
