"""Run selected engineering suites serially against a host, stopping at the first failure."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import sys
import uuid
from mcp_client import McpClient

root = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('regression', root/'eng/run-regression.py')
regression = importlib.util.module_from_spec(spec); spec.loader.exec_module(regression)
SUITES = {'p3': 'p3-engineering.json', 'p4': 'p4-exchange.json', 'p5': 'p5-engineering.json', 'p6': 'p6-incremental.json', 'p7': 'p7-members-batch.json', 'p8': 'p8-graph-v2.json', 'p11': 'p11-initializers.json', 'p12': 'p12-engineering-semantics.json', 'p13': 'p13-inline-types.json'}


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--variables', required=True); parser.add_argument('--report', required=True)
    parser.add_argument('--suite', choices=['all', *SUITES], default='all'); parser.add_argument('--expect-mode', choices=['fixture','real'], default='real')
    parser.add_argument('--url', default='http://127.0.0.1:5088/mcp'); parser.add_argument('--poll-timeout', type=int, default=660)
    args = parser.parse_args(); key = os.environ.get('PLCMCP_API_KEY') or os.environ.get('PlcMcp__ApiKey')
    if not key: parser.error('Set the MCP API key in the environment.')
    report = dict(schemaVersion=1, succeeded=False, compatibilityCertified=False, suites=[])
    try:
        variables = json.loads(Path(args.variables).read_text(encoding='utf-8')); client = McpClient(args.url, key)
        for name in SUITES if args.suite == 'all' else [args.suite]:
            values = dict(variables, runId=uuid.uuid4().hex[:16])
            scenario = json.loads((root/'config/regression'/SUITES[name]).read_text(encoding='utf-8'))
            result = regression.run(client, scenario, values, args.expect_mode, args.poll_timeout)
            report['suites'].append(dict(suite=name, result=result))
            if not result['succeeded']: break
        report['succeeded'] = bool(report['suites']) and all(s['result']['succeeded'] for s in report['suites'])
    except Exception as error: report['error'] = str(error).replace(key, '[redacted]')
    output = Path(args.report).resolve(); output.parent.mkdir(parents=True, exist_ok=True)
    temporary = output.with_suffix(output.suffix+'.tmp'); temporary.write_text(json.dumps(report, ensure_ascii=False, indent=2)+'\n', encoding='utf-8'); temporary.replace(output)
    print(json.dumps(dict(succeeded=report['succeeded'], suites=len(report['suites']), report=str(output), compatibilityCertified=False)))
    return 0 if report['succeeded'] else 1


if __name__ == '__main__': sys.exit(main())
