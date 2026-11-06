"""Full HTTP batch, bounded large-file and ZIP delivery regression. Synthetic fixture only."""
import argparse
import base64
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
from transfer import upload_xml, download_delivery

parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', default='dotnet'); args = parser.parse_args()
root = Path(__file__).resolve().parents[1]
host = root/'tests/PlcMcp.TestHost/bin/Release/net8.0/PlcMcp.TestHost.dll'
spec = importlib.util.spec_from_file_location('regression', root/'eng/run-regression.py'); regression = importlib.util.module_from_spec(spec); spec.loader.exec_module(regression)
try: urllib.request.urlopen('http://127.0.0.1:5088/health/live', timeout=1).close()
except OSError: pass
else: raise RuntimeError('Port 5088 is occupied; this check will not stop another host.')
with tempfile.TemporaryDirectory(prefix='plcmcp-delivery-check-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
    directory = Path(tmp); workspace = directory/'workspaces'
    subprocess.run([shutil.which('python') or 'python', str(root/'eng/init-fixture.py'), '--workspace-root', str(workspace)], check=True, stdout=subprocess.DEVNULL)
    key = secrets.token_hex(32)
    env = dict(os.environ, PlcMcp__ApiKey=key, DOTNET_ENVIRONMENT='Development', PlcMcp__DataDirectory=str(directory/'data'), PlcMcp__WorkspaceRoot=str(workspace),
               PlcMcp__Exchange__MaxXmlBytes='1048576', DOTNET_HOST_PATH=str(Path(shutil.which(args.dotnet) or args.dotnet).resolve()))
    process = subprocess.Popen([args.dotnet, str(host)], env=env, cwd=host.parent, stdout=log, stderr=log)
    try:
        for _ in range(300):
            if process.poll() is not None: raise RuntimeError('Fixture host exited.')
            try: urllib.request.urlopen('http://127.0.0.1:5088/health/live', timeout=1).close(); break
            except OSError: time.sleep(0.1)
        else: raise TimeoutError('Fixture host startup timed out.')
        client = McpClient('http://127.0.0.1:5088/mcp', key)
        assert len(client.rpc('tools/list')['tools']) == 73
        subprocess.run([sys.executable, str(root/'eng/doctor.py'), '--mode', 'fixture'], env=dict(env, PLCMCP_API_KEY=key), check=True)
        if os.environ.get('PLCMCP_OPENCODE'):
            subprocess.run([sys.executable, str(root/'eng/verify-opencode.py'), '--opencode', os.environ['PLCMCP_OPENCODE']],
                           env=dict(env, PLCMCP_API_KEY=key), check=True, timeout=150)
        variables = json.loads((root/'config/regression/fixture-variables.json').read_text()); variables['runId'] = secrets.token_hex(8)
        scenario = json.loads((root/'config/regression/p5-engineering.json').read_text())
        report = regression.run(client, scenario, variables, 'fixture', 120)
        if not report['succeeded']: raise AssertionError(json.dumps(report['cases'][-1], ensure_ascii=False, indent=2))
        package = directory/'delivery.zip'
        download_delivery(client, variables['packageId'], package, variables['packageSha'])
        with zipfile.ZipFile(package) as archive:
            manifest = json.loads(archive.read('manifest.json'))
            assert manifest['evidenceKind'] == 'simulated' and not manifest['engineeringReady']
            for stamp in manifest['files']:
                raw = archive.read('project/'+stamp['path'])
                assert len(raw) == stamp['length'] and hashlib.sha256(raw).hexdigest() == stamp['sha256']
            assert json.loads(archive.read('reports/compile.json'))['succeeded']
            assert json.loads(archive.read('reports/tasks.json'))['configuration']['data']['tasks']
        # Valid, original PLCopen ST content exceeding the old inline limit; no vendor project is supplied.
        xml = ('<project xmlns="http://www.plcopen.org/xml/tc6_0201" xmlns:x="http://www.w3.org/1999/xhtml">'
               '<fileHeader companyName="PlcMcpPlatform" productName="Fixture" productVersion="1" creationDateTime="2000-01-01T00:00:00Z"/>'
               '<contentHeader name="Bulk"><coordinateInfo/></contentHeader><types><dataTypes/><pous><pou name="Bulk" pouType="program"><interface/>'
               '<body><ST><x:xhtml>' + '(* original large exchange sample 测试 *)\n' * 4000 + '</x:xhtml></ST></body></pou></pous></types><instances><configurations/></instances></project>')
        source = directory/'large.xml'; source.write_bytes(('\ufeff'+xml).encode('utf-8'))
        artifact = upload_xml(client, source, 'plcopen', 'large-'+variables['runId'])
        assert artifact['byteLength'] > 65536 and artifact == upload_xml(client, source, 'plcopen', 'large-'+variables['runId'])
        invalid = client.call('plc_exchange_upload_begin', dict(uploadKey='too-large', format='plcopen', byteLength=1048577, sha256='0'*64), allow_error=True)
        assert invalid['error']['code'] == 'invalid_upload_request'
        job = client.call('plc_project_import', dict(workspaceName='p5-'+variables['runId'], expectedRevision=variables['revision'], parentId=variables['applicationId'], artifactId=artifact['artifactId'], idempotencyKey='large-import'))['job']
        result = client.wait(job['jobId'], 120); assert result['status'] == 'Succeeded', result
        readback = result['result']['engineeringResult']['exchange']
        exported = client.call('plc_project_export', dict(projectPath=result['result']['projectPath'], objectIds=readback['objectIds'], format='plcopen', idempotencyKey='large-export'))['job']
        exported = client.wait(exported['jobId'], 120); assert exported['status'] == 'Succeeded', exported
        final_artifact = exported['result']['exchange']['artifact']; assert final_artifact['byteLength'] > 65536
        first = client.call('plc_exchange_read', dict(artifactId=final_artifact['artifactId'], maxChars=512))['value']; assert not first['complete']
        requests = [json.loads(p.read_text()) for p in (directory/'data/invocations').rglob('request.json')]
        imports = [r for r in requests if r['operation']=='project.import' and r['payload']['edit']['exchange']['artifactId']==artifact['artifactId']]
        assert len(imports)==1 and imports[0]['payload']['edit']['exchange']['xml'] is None
        assert imports[0]['payload']['edit']['exchange']['file']['name']=='exchange-input.xml'
        assert all(p.stat().st_size <= 131072 for p in (directory/'data/invocations').rglob('request.json'))
        assert list((directory/'data/invocations').rglob('exchange-output.xml'))
        print('P5 HTTP regression: 73 tools, 18 scenario cases, large UTF-8 file transport and complete ZIP integrity passed. Fixture only.')
    except Exception:
        log.seek(0); print(log.read()[-6000:]); raise
    finally:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
