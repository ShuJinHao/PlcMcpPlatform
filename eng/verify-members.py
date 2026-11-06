"""Member, accessor and shared-configuration HTTP workflow. Fixture data only; no vendor compiler or LLM."""
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
with tempfile.TemporaryDirectory(prefix='plcmcp-members-check-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
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
        scenario = json.loads((root/'config/regression/p7-members-batch.json').read_text(encoding='utf-8'))
        report = regression.run(client, scenario, variables, 'fixture', 240)
        if not report['succeeded']: raise AssertionError(json.dumps(report['cases'][-1], ensure_ascii=False, indent=2))
        package = directory/'members.zip'; download_delivery(client, variables['packageId'], package, variables['packageSha'])
        with zipfile.ZipFile(package) as archive:
            manifest = json.loads(archive.read('manifest.json')); assert manifest['revision'] == variables['patchedRevision']
            for stamp in manifest['files']:
                raw = archive.read('project/'+stamp['path'])
                assert hashlib.sha256(raw).hexdigest() == stamp['sha256'] and len(raw) == stamp['length']
        name = 'p7-'+variables['runId']; current = variables['patchedRevision']
        query = dict(workspaceName=name, expectedHeadRevision=current, revision=current, applicationId=variables['applicationId'], objectId=variables['resetId'], maxTextChars=8192)
        actual = job('plc_project_objects_read', query)['result']['items'][0]['implementation']
        invalid = dict(applicationId=variables['applicationId'], steps=[dict(id='compile_error', operation='text.patch', change=dict(objectId=variables['resetId'],
            patch=dict(section='implementation', expectedSha256=actual['sha256'], oldText=actual['text'], newText='FIXTURE_ERROR', memberKind='Method')))])
        preview = client.call('plc_project_batch_preview', dict(workspaceName=name, expectedRevision=current, plan=invalid))['value']
        failed = job('plc_project_batch_apply', dict(workspaceName=name, expectedRevision=current, plan=invalid, expectedPlanSha256=preview['planSha256']), 'Failed')
        assert failed['errorCode'] == 'compile_failed'
        assert client.call('plc_workspace_get', dict(workspaceName=name))['value']['revision'] == current
        # Reusing a pre-publication continuation with the new head must fail before a worker starts.
        stale = client.call('plc_project_objects_read', dict(workspaceName=name, expectedHeadRevision=current, revision=current,
            applicationId=variables['applicationId'], collection='members', parentId=variables['motorId'], pageSize=2,
            cursor=variables['memberCursor'], idempotencyKey='stale-cursor'), allow_error=True)
        assert stale['error']['code'] == 'engineering_cursor_query_mismatch'
        print('P7 HTTP regression: 74 tools, 22 scenario cases, 28-step engineering batch, member compile failure and revision-bound cursors passed. Fixture only.')
    except Exception:
        log.seek(0); print(log.read()[-6000:]); raise
    finally:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
