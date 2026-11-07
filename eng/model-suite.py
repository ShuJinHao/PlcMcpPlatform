"""Run independent model workflows serially; preserve failures and server evidence.

Each run creates a new workspace. A failure stops the suite without retrying writes.
"""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
from mcp_client import McpClient
from model_scenarios import walk
from transfer import download_delivery

ROOT = Path(__file__).resolve().parents[1]
RUNS = [('repair', 'repair'), ('graphics-1', 'graphics'), ('graphics-2', 'graphics'),
        ('graphics-3', 'graphics'), ('members', 'members'), ('configuration', 'configuration'),
        ('incremental', 'incremental'), ('declarations', 'declarations')]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    temp = path.with_suffix(path.suffix+'.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    temp.replace(path)


def collect(client, directory, report):
    jobs = [client.call('plc_job_get', dict(jobId=i))['job'] for i in report['jobIds']]
    head = client.call('plc_workspace_get', dict(workspaceName=report['workspaceName']))['value']
    if head['revision'] != report['finalRevision']: raise ValueError('head_changed_during_evidence_collection')
    if any(j['status'] in ('Queued', 'Running', 'CancelRequested', 'OutcomeUnknown') for j in jobs):
        raise ValueError('unresolved_job_during_evidence_collection')
    packages = [r['artifact'] for j in jobs if j['operation'] == 'project.package' and j['status'] == 'Succeeded'
                for r in walk(j['result']) if isinstance(r.get('artifact'), dict)
                and r['artifact'].get('revision') == head['revision'] and r['artifact'].get('sha256') == report['deliverySha256']]
    if not packages: raise ValueError('exact_delivery_missing_during_collection')
    write(directory/'server-evidence.json', dict(workspace=head, jobs=jobs, evidenceKind=report['evidenceKind']))
    artifact = packages[-1]
    download_delivery(client, artifact['artifactId'], directory/'delivery.zip', artifact['sha256'])


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--report-directory', required=True); parser.add_argument('--execute', action='store_true')
    parser.add_argument('--model', default='deepseek/deepseek-flash'); parser.add_argument('--opencode', default='opencode')
    parser.add_argument('--provider-config', default=str(ROOT/'config/deepseek.example.json'))
    parser.add_argument('--provider-env', action='append', default=[])
    parser.add_argument('--variables', default=str(ROOT/'config/regression/fixture-variables.json'))
    parser.add_argument('--url', default='http://127.0.0.1:5088/mcp'); parser.add_argument('--timeout', type=int, default=1200)
    parser.add_argument('--expect-mode', choices=['fixture', 'real'], default='fixture')
    args = parser.parse_args(); destination = Path(args.report_directory).resolve()
    if destination.exists(): parser.error('Choose a new directory; prior evidence is never overwritten.')
    destination.mkdir(parents=True)
    summary = dict(schemaVersion=1, status='skipped', reason='execution_requires_execute', model=args.model,
                   startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(), runs=[],
                   llmGenerationVerified=False, realIdeScenarioPassed=False, engineeringReady=False)
    code = 3
    try:
        if args.execute:
            key = os.environ.get('PLCMCP_API_KEY') or os.environ.get('PlcMcp__ApiKey')
            if not key: raise ValueError('mcp_key_missing')
            client = McpClient(args.url, key)
            summary.update(status='running', reason=None)
            for name, scenario in RUNS:
                output = destination/name; output.mkdir()
                command = [sys.executable, str(ROOT/'eng/model-regression.py'), '--execute', '--scenario', scenario,
                           '--report', str(output/'report.json'), '--model', args.model, '--opencode', args.opencode,
                           '--provider-config', args.provider_config, '--variables', args.variables,
                           '--url', args.url, '--timeout', str(args.timeout), '--expect-mode', args.expect_mode]
                for env in args.provider_env: command.extend(['--provider-env', env])
                # model-regression writes the authoritative report; never print provider streams.
                result = subprocess.run(command, cwd=ROOT, stdout=subprocess.DEVNULL)
                report = json.loads((output/'report.json').read_text(encoding='utf-8'))
                row = dict(name=name, scenario=scenario, status=report['status'], report=name+'/report.json',
                           reportSha256=digest(output/'report.json'))
                for field in ('workspaceName', 'toolCalls', 'invalidToolCalls', 'failedToolCalls', 'elapsedSeconds', 'modelDurationSeconds', 'toolRejectionCodes', 'finalRevision', 'deliverySha256', 'errorCode'):
                    if field in report: row[field] = report[field]
                summary['runs'].append(row); write(destination/'summary.json', summary)
                if result.returncode or report['status'] != 'passed':
                    summary.update(status=report['status'], reason='suite_stopped_at_'+name)
                    code = result.returncode or 2; break
                collect(client, output, report)
                row.update(serverEvidence=name+'/server-evidence.json', serverEvidenceSha256=digest(output/'server-evidence.json'), delivery=name+'/delivery.zip')
                write(destination/'summary.json', summary)
                print(json.dumps(dict(run=name, status='passed', toolCalls=report['toolCalls'])), flush=True)
            else:
                summary.update(status='passed', llmGenerationVerified=True, realIdeScenarioPassed=args.expect_mode == 'real',
                               toolCallsInPassedRuns=sum(r['toolCalls'] for r in summary['runs']))
                code = 0
    except Exception as error:
        summary.update(status='failed', reason=type(error).__name__)
        code = 2
    finally:
        summary['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        write(destination/'summary.json', summary)
    print(json.dumps(dict(status=summary['status'], report=str(destination/'summary.json'))))
    return code


if __name__ == '__main__': sys.exit(main())
