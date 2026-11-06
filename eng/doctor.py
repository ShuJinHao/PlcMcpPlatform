"""Read-only host diagnostics. Does not compile, alter a project, unlock or retry jobs."""
import argparse
import json
import os
from pathlib import Path
import sys
from mcp_client import McpClient


def inspect(client, mode):
    runtime = client.call('plc_runtime_get_status', {})
    names = sorted(t['name'] for t in client.rpc('tools/list')['tools'])
    contracts = {t['name'] for t in json.loads((Path(__file__).resolve().parents[1]/'contracts/mcp-tools.v1.json').read_text(encoding='utf-8'))}
    inventory = runtime.get('tools')
    if not isinstance(inventory, list) or {t['name'] for t in inventory} != contracts: raise ValueError('Runtime access inventory differs from contract.')
    expected = sorted(t['name'] for t in inventory if t['available'])
    if names != expected: raise ValueError('Discovery and runtime access policy differ; retry if the environment changed.')
    if runtime['executionMode'] != ('fixture' if mode == 'fixture' else 'real'): raise ValueError('Host execution mode does not match.')
    if mode == 'validation' and not runtime.get('validation'): raise ValueError('The explicit ValidationHost is required.')
    if mode != 'validation' and runtime.get('validation'): raise ValueError('Unexpected validation session.')
    acceptance = runtime.get('readiness') or {}
    if runtime['engineeringReady'] and (mode != 'production' or acceptance.get('state') != 'accepted' or runtime['schedulerBlocked']):
        raise ValueError('Readiness is inconsistent with reviewed scope and scheduler state.')
    capabilities = client.call('plc_capabilities_list', {})['capabilities']
    for capability in capabilities:
        if capability['status'] == 'Supported' and (acceptance.get('state') != 'accepted' or not capability.get('acceptedScopes')):
            raise ValueError('Supported capability has no reviewed scope.')
    return dict(schemaVersion=1, mode=mode, toolCount=len(names), contractVersion=runtime['contractVersion'],
                schedulerBlocked=runtime['schedulerBlocked'], connectionVerified=True,
                evidenceKind=runtime['evidenceKind'], engineeringReady=runtime['engineeringReady'], readiness=acceptance,
                validation=runtime.get('validation'), localToolsAvailable=sum(t['kind']=='local' and t['available'] for t in inventory),
                succeeded=not runtime['schedulerBlocked'] and acceptance.get('state') != 'unavailable')


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--url', default='http://127.0.0.1:5088/mcp')
    parser.add_argument('--mode', choices=['production', 'fixture', 'validation'], required=True); parser.add_argument('--report')
    args = parser.parse_args(); key = os.environ.get('PLCMCP_API_KEY') or os.environ.get('PlcMcp__ApiKey')
    if not key: parser.error('Set PLCMCP_API_KEY or PlcMcp__ApiKey in the environment.')
    try: report = inspect(McpClient(args.url, key), args.mode)
    except Exception as error: report = dict(schemaVersion=1, succeeded=False, error=str(error).replace(key, '[redacted]'), engineeringReady=False)
    text = json.dumps(report, ensure_ascii=False, indent=2)
    if args.report:
        output = Path(args.report); output.parent.mkdir(parents=True, exist_ok=True); output.write_text(text+'\n', encoding='utf-8')
    print(text); return 0 if report['succeeded'] else 1


if __name__ == '__main__': sys.exit(main())
