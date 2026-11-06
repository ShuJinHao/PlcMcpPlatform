"""Cross-process ownership, retention expiry and timed SFC transport checks; fixture only."""
import argparse
import base64
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import tempfile
import time
import urllib.request
from mcp_client import McpClient


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--dotnet',default='dotnet');args=parser.parse_args()
    root=Path(__file__).resolve().parents[1];host=root/'tests/PlcMcp.TestHost/bin/Release/net8.0/PlcMcp.TestHost.dll'
    url='http://127.0.0.1:5088/mcp'
    try: urllib.request.urlopen('http://127.0.0.1:5088/health/live',timeout=1).close()
    except OSError: pass
    else: raise RuntimeError('Port 5088 is occupied.')
    with tempfile.TemporaryDirectory(prefix='plcmcp-maintenance-') as tmp, tempfile.TemporaryFile(mode='w+') as log:
        base=Path(tmp); data=base/'data';workspace=base/'workspace';workspace.mkdir()
        key=secrets.token_hex(32)
        env=dict(os.environ,PlcMcp__ApiKey=key,PlcMcp__DataDirectory=str(data),PlcMcp__WorkspaceRoot=str(workspace),DOTNET_ENVIRONMENT='Development')
        def start():
            process=subprocess.Popen([args.dotnet,str(host)],cwd=host.parent,env=env,stdout=log,stderr=log)
            for _ in range(200):
                if process.poll() is not None: log.seek(0); raise RuntimeError(log.read()[-4000:])
                try: urllib.request.urlopen('http://127.0.0.1:5088/health/live',timeout=1).close();return process
                except OSError: time.sleep(.1)
            process.kill();process.wait();raise TimeoutError('Host startup timed out.')
        def stop(process):
            if process.poll() is None:
                process.terminate()
                try:process.wait(timeout=10)
                except subprocess.TimeoutExpired:process.kill();process.wait(timeout=10)
        command=[args.dotnet,str(host),'--maintenance']
        common=['--data-directory',str(data),'--workspace-root',str(workspace)]
        plan=base/'preview.json';process=start()
        try:
            client=McpClient(url,key);body=b'not-yet-complete-xml'
            session=client.call('plc_exchange_upload_begin',dict(uploadKey='maintenance',format='plcopen',byteLength=len(body),sha256=hashlib.sha256(body).hexdigest()))['value']
            client.call('plc_exchange_upload_append',dict(uploadId=session['uploadId'],offset=0,base64=base64.b64encode(body).decode()))
            busy=subprocess.run(command+['preview']+common+['--report',str(plan)],capture_output=True,text=True)
            assert busy.returncode==2 and not plan.exists(),busy.stdout+busy.stderr
            graph=json.loads((root/'tests/fixtures/graphs/sfc-v2-timed.json').read_text())
            generated=client.call('plc_graph_generate',dict(program=graph))
            assert not generated.get('error'),generated
        finally:stop(process)
        for path in (data/'uploads').glob('*'):os.utime(path,(946684800,946684800))
        subprocess.run(command+['preview']+common+['--report',str(plan)],check=True)
        preview=json.loads(plan.read_text());assert len(preview['items'])==1
        subprocess.run(command+['apply']+common+['--plan',str(plan)],check=True)
        subprocess.run(command+['apply']+common+['--plan',str(plan)],check=True)
        process=start()
        try:
            client=McpClient(url,key)
            expired=client.call('plc_exchange_upload_complete',dict(uploadId=session['uploadId']),allow_error=True)
            assert expired['error']['code']=='upload_expired',expired
            assert not client.call('plc_runtime_get_status',{})['schedulerBlocked']
        finally:stop(process)
        skipped=base/'model-skipped.json'
        result=subprocess.run([sys.executable,str(root/'eng/model-regression.py'),'--report',str(skipped)])
        assert result.returncode==3 and json.loads(skipped.read_text())['llmGenerationVerified'] is False
        print('P10: active-host exclusion, preview/apply/replay, persisted expiry, timed SFC tool and explicit model skip passed.')


if __name__=='__main__':main()
