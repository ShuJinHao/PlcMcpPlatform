"""Run an explicit MCP scenario against an already started validation/fixture host.

Never starts an IDE, promotes capability status, retries writes or sends PLC online commands.
Reports remain local and may contain engineering data. Do not commit real reports/projects.
"""
import argparse
import datetime
import json
import os
from pathlib import Path
import re
import sys
import time
import uuid
from mcp_client import McpClient


def pointer(value, path):
    if path == '': return value
    if not path.startswith('/'): raise ValueError('Use RFC6901 JSON pointers in binds and expectations.')
    for part in path[1:].split('/'):
        key = part.replace('~1','/').replace('~0','~')
        value = value[int(key)] if isinstance(value,list) else value[key]
    return value


def resolve(value, variables):
    if isinstance(value, dict): return {k:resolve(v,variables) for k,v in value.items()}
    if isinstance(value, list): return [resolve(v,variables) for v in value]
    if not isinstance(value,str): return value
    full = re.fullmatch(r'\$\{([A-Za-z_][A-Za-z0-9_]*)\}',value)
    if full: return variables[full.group(1)]
    return re.sub(r'\$\{([A-Za-z_][A-Za-z0-9_]*)\}',lambda m:str(variables[m.group(1)]),value)


def run(client, scenario, variables, expected_mode, timeout):
    report = dict(schemaVersion=1, startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  runId=variables['runId'], expectedExecutionMode=expected_mode, compatibilityCertified=False, cases=[], succeeded=False)
    try:
        runtime=client.call('plc_runtime_get_status',{})
        report['runtime']=runtime
        if runtime['executionMode'] != expected_mode: raise ValueError('Unexpected execution mode; refusing this scenario.')
        if expected_mode == 'real' and not runtime.get('validation'): raise ValueError('Start the explicit Windows ValidationHost first.')
        if runtime['schedulerBlocked']: raise ValueError('Executor is blocked; inspect durable evidence before regression.')
        tools={t['name']:t for t in client.rpc('tools/list')['tools']}
        if scenario.get('schemaVersion') != 1 or not scenario.get('cases') or len(scenario['cases']) > 256: raise ValueError('Invalid bounded scenario.')
        ids=[c['id'] for c in scenario['cases']]
        if len(ids)!=len(set(ids)): raise ValueError('Case ids must be unique.')
        for case in scenario['cases']:
            item=dict(id=case['id'],tool=case['tool'],succeeded=False); report['cases'].append(item)
            start=time.monotonic()
            try:
                if case['tool'] not in tools: raise ValueError('Tool not registered: '+case['tool'])
                arguments=resolve(case.get('arguments',{}),variables)
                properties=tools[case['tool']]['inputSchema'].get('properties',{})
                if 'idempotencyKey' in properties and 'idempotencyKey' not in arguments:
                    arguments['idempotencyKey']=variables['runId']+'-'+case['id']
                reply=client.call(case['tool'],arguments,allow_error=case.get('expectToolError',False))
                item['reply']=reply
                if reply.get('executionMode', expected_mode) != expected_mode: raise ValueError('Execution mode changed.')
                result=client.wait(reply['job']['jobId'],timeout) if reply.get('job') else reply
                item['result']=result
                if result.get('status') in ('OutcomeUnknown','CancelRequested'): raise ValueError('Outcome uncertain; subsequent writes were stopped.')
                expectations=case.get('expect',[])
                if result.get('status') and not any(e['path']=='/status' for e in expectations): expectations=[dict(path='/status',equals='Succeeded')]+expectations
                for expectation in expectations:
                    actual=pointer(result,expectation['path']); wanted=resolve(expectation['equals'],variables)
                    if actual!=wanted: raise AssertionError('Expectation mismatch at '+expectation['path'])
                if case.get('expectToolError') and not reply.get('error'): raise AssertionError('Expected a rejected tool request.')
                for key,path in case.get('bind',{}).items(): variables[key]=pointer(result,path)
                item['succeeded']=True
            except Exception as error:
                item['error']=str(error); raise
            finally: item['durationSeconds']=round(time.monotonic()-start,3)
        report['succeeded']=True
    except Exception as error: report['error']=str(error)
    report['finishedUtc']=datetime.datetime.now(datetime.timezone.utc).isoformat()
    return report


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument('--scenario',required=True); parser.add_argument('--variables',required=True)
    parser.add_argument('--report',required=True); parser.add_argument('--url',default='http://127.0.0.1:5088/mcp')
    parser.add_argument('--expect-mode',choices=['real','fixture'],default='real'); parser.add_argument('--poll-timeout',type=int,default=660)
    args=parser.parse_args()
    key=os.environ.get('PLCMCP_API_KEY')
    if not key: parser.error('Set PLCMCP_API_KEY in the local environment.')
    variables=json.loads(Path(args.variables).read_text(encoding='utf-8')); variables['runId']=uuid.uuid4().hex[:16]
    scenario=json.loads(Path(args.scenario).read_text(encoding='utf-8'))
    try: report=run(McpClient(args.url,key),scenario,variables,args.expect_mode,args.poll_timeout)
    except Exception as error: report=dict(schemaVersion=1,succeeded=False,cases=[],compatibilityCertified=False,error=str(error),runId=variables['runId'])
    output=Path(args.report).resolve(); output.parent.mkdir(parents=True,exist_ok=True)
    temporary=output.with_suffix(output.suffix+'.tmp'); temporary.write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8'); temporary.replace(output)
    print(json.dumps(dict(succeeded=report['succeeded'],cases=len(report['cases']),report=str(output),compatibilityCertified=False)))
    return 0 if report['succeeded'] else 1


if __name__=='__main__': sys.exit(main())
