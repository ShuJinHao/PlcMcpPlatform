"""Exercise every P3 tool through HTTP and persisted fixture candidates; also check the regression runner."""
from pathlib import Path
import argparse
import importlib.util
import json
import os
import secrets
import shutil
import subprocess
import tempfile
import time
import urllib.request
from mcp_client import McpClient

parser=argparse.ArgumentParser(); parser.add_argument('--dotnet',default='dotnet'); args=parser.parse_args()
root=Path(__file__).resolve().parents[1]; host=root/'tests/PlcMcp.TestHost/bin/Release/net8.0/PlcMcp.TestHost.dll'
spec=importlib.util.spec_from_file_location('regression',root/'eng/run-regression.py'); regression=importlib.util.module_from_spec(spec); spec.loader.exec_module(regression)
try: urllib.request.urlopen('http://127.0.0.1:5088/health/live',timeout=1).close()
except OSError: pass
else: raise RuntimeError('Port 5088 is occupied; this check will not stop another host.')
with tempfile.TemporaryDirectory(prefix='plcmcp-config-check-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
    directory=Path(tmp); workspace=directory/'workspaces'
    subprocess.run([shutil.which('python') or 'python',str(root/'eng/init-fixture.py'),'--workspace-root',str(workspace)],check=True,stdout=subprocess.DEVNULL)
    key=secrets.token_hex(32)
    env=dict(os.environ,PlcMcp__ApiKey=key,DOTNET_ENVIRONMENT='Development',PlcMcp__DataDirectory=str(directory/'data'),PlcMcp__WorkspaceRoot=str(workspace),DOTNET_HOST_PATH=str(Path(shutil.which(args.dotnet) or args.dotnet).resolve()))
    process=subprocess.Popen([args.dotnet,str(host)],env=env,cwd=host.parent,stdout=log,stderr=log)
    try:
        for _ in range(300):
            if process.poll() is not None: raise RuntimeError('Fixture host exited.')
            try: urllib.request.urlopen('http://127.0.0.1:5088/health/live',timeout=1).close(); break
            except OSError: time.sleep(0.1)
        else: raise TimeoutError('Fixture host startup timed out.')
        client=McpClient('http://127.0.0.1:5088/mcp',key)
        tools=client.rpc('tools/list')['tools']; assert len(tools)==73,len(tools)
        for tool in tools:
            assert tool['inputSchema']['type']=='object'; assert tool['outputSchema']['type']=='object'
        variables=json.loads((root/'config/regression/fixture-variables.json').read_text()); variables['runId']=secrets.token_hex(8)
        scenario=json.loads((root/'config/regression/p3-engineering.json').read_text())
        report=regression.run(client,scenario,variables,'fixture',30)
        if not report['succeeded']: raise AssertionError(json.dumps(report['cases'][-1],ensure_ascii=False,indent=2))
        assert len(report['cases'])==len(scenario['cases'])
        mismatch=regression.run(client,scenario,dict(variables,runId=secrets.token_hex(8)),'real',30)
        assert not mismatch['succeeded'] and not mismatch['cases']
        rejected=client.call('plc_device_set_io_mapping',dict(workspaceName='p3-'+variables['runId'],expectedRevision=variables['revision'],deviceId=variables['controllerId'],address=dict(parameterId=1),variable='Unqualified',createVariable=False,idempotencyKey='invalid-mapping'),allow_error=True)
        assert rejected['error']['code']=='invalid_io_expression',rejected
        print('P3 HTTP regression: 73 tools, '+str(len(report['cases']))+' completed cases; wrong-mode and invalid-mapping checks passed. Simulated only.')
    except Exception:
        log.seek(0); print(log.read()[-6000:]); raise
    finally:
        if process.poll() is None:
            process.terminate()
            try: process.wait(timeout=10)
            except subprocess.TimeoutExpired: process.kill(); process.wait(timeout=5)
