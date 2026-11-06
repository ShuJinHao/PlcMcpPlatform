"""Existing-project incremental HTTP workflow. Fixture data only; no vendor compiler or LLM."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import secrets
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.request
import zipfile
from mcp_client import McpClient
from transfer import download_delivery

parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', default='dotnet'); args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
host = root/'tests/PlcMcp.TestHost/bin/Release/net8.0/PlcMcp.TestHost.dll'
spec = importlib.util.spec_from_file_location('regression', root/'eng/run-regression.py')
regression = importlib.util.module_from_spec(spec); spec.loader.exec_module(regression)
try: urllib.request.urlopen('http://127.0.0.1:5088/health/live', timeout=1).close()
except OSError: pass
else: raise RuntimeError('Port 5088 is occupied; this check will not stop another host.')
with tempfile.TemporaryDirectory(prefix='plcmcp-incremental-check-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
    directory = Path(tmp); workspace = directory/'workspaces'
    subprocess.run([sys.executable, str(root/'eng/init-fixture.py'), '--workspace-root', str(workspace)], check=True, stdout=subprocess.DEVNULL)
    key = secrets.token_hex(32)
    env = dict(os.environ, PlcMcp__ApiKey=key, DOTNET_ENVIRONMENT='Development', PlcMcp__DataDirectory=str(directory/'data'), PlcMcp__WorkspaceRoot=str(workspace),
               DOTNET_HOST_PATH=str(Path(shutil.which(args.dotnet) or args.dotnet).resolve()))
    process = subprocess.Popen([args.dotnet, str(host)], env=env, cwd=host.parent, stdout=log, stderr=log)
    try:
        for _ in range(300):
            if process.poll() is not None: raise RuntimeError('Fixture host exited.')
            try: urllib.request.urlopen('http://127.0.0.1:5088/health/live', timeout=1).close(); break
            except OSError: time.sleep(0.1)
        else: raise TimeoutError('Fixture host startup timed out.')
        client = McpClient('http://127.0.0.1:5088/mcp', key)
        assert len(client.rpc('tools/list')['tools']) == 74
        variables = json.loads((root/'config/regression/fixture-variables.json').read_text(encoding='utf-8'))
        variables['runId'] = secrets.token_hex(8)
        def job(tool, arguments, status='Succeeded'):
            reply = client.call(tool, dict(arguments, idempotencyKey=secrets.token_hex(12)))
            result = client.wait(reply['job']['jobId'], 120)
            assert result['status'] == status, result
            return result
        # Prepare a pre-existing saved project with an actual fixture task binding, before P6 adoption.
        created = job('plc_project_create', dict(workspaceName='seed', templateId='sample'))['result']
        plan = dict(applicationId=variables['applicationId'], steps=[
            dict(id='configuration', operation='task.configuration.create', change=dict(parentId=variables['applicationId'])),
            dict(id='task', operation='task.create', change=dict(parentId='@configuration', name='MainTask', configuration=dict(task=dict(kind='Cyclic', priority='1', interval='20', intervalUnit='ms')))),
            dict(id='calls', operation='task.calls.set', change=dict(objectId='@task', configuration=dict(calls=[dict(pouId=variables['programId'], pouName=variables['programName'])])))])
        preview = client.call('plc_project_batch_preview', dict(workspaceName='seed', expectedRevision=created['revision'], plan=plan))['value']
        seeded = job('plc_project_batch_apply', dict(workspaceName='seed', expectedRevision=created['revision'], plan=plan, expectedPlanSha256=preview['planSha256']))['result']
        source = workspace/'imports/existing'; source.mkdir(parents=True)
        source_file = source/'project.fixture.json'; shutil.copy2(seeded['projectPath'], source_file)
        original = source_file.read_bytes()
        scenario = json.loads((root/'config/regression/p6-incremental.json').read_text(encoding='utf-8'))
        report = regression.run(client, scenario, variables, 'fixture', 120)
        if not report['succeeded']: raise AssertionError(json.dumps(report['cases'][-1], ensure_ascii=False, indent=2))
        assert source_file.read_bytes() == original
        package = directory/'incremental.zip'; download_delivery(client, variables['packageId'], package, variables['packageSha'])
        with zipfile.ZipFile(package) as archive:
            manifest = json.loads(archive.read('manifest.json')); assert manifest['revision'] == variables['revision']
            for stamp in manifest['files']:
                raw = archive.read('project/'+stamp['path'])
                assert hashlib.sha256(raw).hexdigest() == stamp['sha256'] and len(raw) == stamp['length']
            calls = json.loads(archive.read('reports/tasks.json'))['configuration']['data']['tasks'][0]['calls']
            assert calls[0]['pouId'] == variables['programId'] and calls[0]['resolved']
        name = 'p6-'+variables['runId']; current = variables['restoredRevision']
        invalid = dict(applicationId=variables['applicationId'], steps=[dict(id='compile_error', operation='text.patch', change=dict(objectId=variables['programId'],
            patch=dict(section='implementation', expectedSha256=variables['sourceSha'], oldText=variables['oldImplementation'], newText='FIXTURE_ERROR')))])
        preview = client.call('plc_project_batch_preview', dict(workspaceName=name, expectedRevision=current, plan=invalid))['value']
        failed = job('plc_project_batch_apply', dict(workspaceName=name, expectedRevision=current, plan=invalid, expectedPlanSha256=preview['planSha256']), 'Failed')
        assert failed['errorCode'] == 'compile_failed'
        assert client.call('plc_workspace_get', dict(workspaceName=name))['value']['revision'] == current
        assert client.call('plc_project_history', dict(workspaceName=name, expectedHeadRevision=current))['value']['versions'][-1]['revision'] == current
        # Source changed after preview: adoption cannot silently accept a newer saved file.
        source_definition = variables['adoptionSource']
        preview = client.call('plc_project_adopt_preview', dict(source=source_definition))['value']
        source_file.write_bytes(original+b' ')
        rejected = client.call('plc_project_adopt', dict(workspaceName='stale-source', source=source_definition, expectedSourceRevision=preview['sourceRevision'], idempotencyKey='stale'), allow_error=True)
        assert rejected['error']['code'] == 'workspace_revision_conflict'
        # Unknown saved-candidate result blocks subsequent execution and is not force-reconciled.
        fault = json.loads(original); fault['fault'] = 'after_save_exit'; source_file.write_text(json.dumps(fault), encoding='utf-8')
        preview = client.call('plc_project_adopt_preview', dict(source=source_definition))['value']
        unknown = job('plc_project_adopt', dict(workspaceName='uncertain-source', source=source_definition, expectedSourceRevision=preview['sourceRevision']), 'OutcomeUnknown')
        assert client.call('plc_job_reconcile', dict(jobId=unknown['jobId']), allow_error=True)['error']['code'] == 'reconciliation_unproven'
        assert client.call('plc_runtime_get_status', {})['schedulerBlocked']
        print('P6 HTTP regression: 74 tools, 14 scenario cases, existing task binding, exact ZIP, compile failure, stale source and unknown-outcome quarantine passed. Fixture only.')
    except Exception:
        log.seek(0); print(log.read()[-6000:]); raise
    finally:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
