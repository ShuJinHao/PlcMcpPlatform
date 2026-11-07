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
import uuid
from mcp_client import McpClient
from model_scenarios import walk
from transfer import download_delivery

ROOT = Path(__file__).resolve().parents[1]
RUNS = [('repair', 'repair'), ('graphics-1', 'graphics'), ('graphics-2', 'graphics'),
        ('graphics-3', 'graphics'), ('members', 'members'), ('configuration', 'configuration'),
        ('incremental', 'incremental'), ('declarations', 'declarations')]
TERMINAL_JOB_STATUSES = frozenset(('Succeeded', 'Failed', 'Cancelled'))
DELIVERY_ERRORS = frozenset(('model_run_not_passed', 'incomplete_server_evidence',
                           'unresolved_job_during_evidence_collection',
                           'head_changed_during_evidence_collection',
                           'exact_delivery_missing_during_collection'))


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def write(path, value):
    temp = path.with_suffix(path.suffix+'.tmp')
    temp.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    temp.replace(path)


def collect_evidence(client, directory, report):
    """Save a bounded, best-effort snapshot, including failures and unresolved jobs.

    Reads are independent and never retried. A complete snapshot does not mean
    the observed jobs are terminal; their original status remains authoritative.
    """
    errors = []
    identities = []
    for field in ('jobIds', 'observedJobIds'):
        values = report.get(field, [])
        if not isinstance(values, list):
            errors.append(dict(target='report', code=field+'_invalid'))
            continue
        invalid = False
        overflow = False
        for value in values:
            try:
                if not isinstance(value, str): raise ValueError()
                identity = str(uuid.UUID(value))
            except (ValueError, AttributeError):
                invalid = True
                continue
            if identity in identities: continue
            if len(identities) >= 128:
                overflow = True
                continue
            identities.append(identity)
        if invalid: errors.append(dict(target='report', code=field+'_invalid'))
        if overflow: errors.append(dict(target='report', code='job_limit_exceeded'))
    snapshot = dict(workspace=None, jobs=[],
                    evidenceKind=report.get('evidenceKind', 'simulated' if report.get('executionMode') == 'fixture' else 'unverified'),
                    requestedJobIds=identities, unresolvedJobIds=[], readErrors=errors, complete=False)
    path = directory/'server-evidence.json'
    # Persist before and after every read so later errors cannot discard prior evidence.
    write(path, snapshot)
    for identity in identities:
        try:
            result = client.call('plc_job_get', dict(jobId=identity))
            job = result['job']
            if not isinstance(job, dict) or str(uuid.UUID(job.get('jobId', ''))) != identity:
                raise ValueError()
        except Exception:
            errors.append(dict(target='job', jobId=identity, code='job_read_failed'))
        else:
            snapshot['jobs'].append(job)
            if not isinstance(job.get('status'), str) or job['status'] not in TERMINAL_JOB_STATUSES:
                snapshot['unresolvedJobIds'].append(identity)
        write(path, snapshot)
    workspace_name = report.get('workspaceName')
    if not isinstance(workspace_name, str) or not workspace_name:
        errors.append(dict(target='workspace', code='workspace_name_missing'))
    else:
        try:
            head = client.call('plc_workspace_get', dict(workspaceName=workspace_name))['value']
            if not isinstance(head, dict) or head.get('workspaceName') != workspace_name or not isinstance(head.get('revision'), str):
                raise ValueError()
        except Exception:
            errors.append(dict(target='workspace', workspaceName=workspace_name, code='workspace_read_failed'))
        else:
            snapshot['workspace'] = head
    snapshot['complete'] = not errors
    write(path, snapshot)
    return snapshot


def collect_delivery(client, directory, report, evidence):
    """Verify success against the saved snapshot before downloading its exact ZIP."""
    if report.get('status') != 'passed': raise ValueError('model_run_not_passed')
    if not evidence.get('complete') or evidence.get('readErrors') or not isinstance(evidence.get('workspace'), dict):
        raise ValueError('incomplete_server_evidence')
    jobs = evidence['jobs']
    if evidence.get('unresolvedJobIds') or any(not isinstance(j.get('status'), str) or j['status'] not in TERMINAL_JOB_STATUSES for j in jobs):
        raise ValueError('unresolved_job_during_evidence_collection')
    if {str(uuid.UUID(j['jobId'])) for j in jobs} != set(evidence['requestedJobIds']):
        raise ValueError('incomplete_server_evidence')
    head = evidence['workspace']
    if not report.get('finalRevision') or head.get('revision') != report['finalRevision']:
        raise ValueError('head_changed_during_evidence_collection')
    packages = [r['artifact'] for j in jobs if j.get('operation') == 'project.package' and j['status'] == 'Succeeded'
                for r in walk(j.get('result')) if isinstance(r.get('artifact'), dict)
                and r['artifact'].get('revision') == head['revision']
                and report.get('deliverySha256') and r['artifact'].get('sha256') == report['deliverySha256']]
    if not packages: raise ValueError('exact_delivery_missing_during_collection')
    artifact = packages[-1]
    return download_delivery(client, artifact['artifactId'], directory/'delivery.zip', artifact['sha256'])


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
                           reportSha256=digest(output/'report.json'), modelStatus=report['status'], exitCode=result.returncode)
                for field in ('workspaceName', 'toolCalls', 'invalidToolCalls', 'failedToolCalls', 'elapsedSeconds', 'modelDurationSeconds', 'toolRejectionCodes', 'finalRevision', 'deliverySha256', 'errorCode'):
                    if field in report: row[field] = report[field]
                summary['runs'].append(row); write(destination/'summary.json', summary)
                evidence = None
                row.update(evidenceComplete=False, unresolvedJobIds=[])
                try:
                    evidence = collect_evidence(client, output, report)
                except OSError:
                    row['evidenceError'] = 'evidence_save_failed'
                except Exception:
                    row['evidenceError'] = 'evidence_collection_failed'
                # A later storage error can leave an earlier partial snapshot. Register
                # that file without treating the unfinished collection as complete.
                evidence_path = output/'server-evidence.json'
                try:
                    if evidence_path.is_file():
                        saved = evidence if evidence is not None else json.loads(evidence_path.read_text(encoding='utf-8'))
                        row.update(serverEvidence=name+'/server-evidence.json', serverEvidenceSha256=digest(evidence_path),
                                   evidenceComplete=bool(saved.get('complete')) and 'evidenceError' not in row,
                                   unresolvedJobIds=saved.get('unresolvedJobIds', []))
                    else:
                        row.setdefault('evidenceError', 'evidence_save_failed')
                except Exception:
                    row.setdefault('evidenceError', 'evidence_registration_failed')
                if result.returncode or report['status'] != 'passed':
                    # Collection errors must not replace the model's original failure,
                    # report, or process exit code. Never request a ZIP on this path.
                    if report['status'] == 'passed': row['status'] = 'failed'
                    summary.update(status=row['status'], reason='suite_stopped_at_'+name)
                    code = result.returncode or 2; break
                if row.get('evidenceError'):
                    row['status'] = 'failed'
                    summary.update(status='failed', reason='evidence_collection_failed_at_'+name)
                    code = 2; break
                try:
                    collect_delivery(client, output, report, evidence)
                except Exception as error:
                    # Only our fixed validation codes are public; tool/transport error
                    # messages can contain private paths, server output or credentials.
                    reason = error.args[0] if isinstance(error, ValueError) and error.args else None
                    row.update(status='failed', deliveryError=reason if isinstance(reason, str) and reason in DELIVERY_ERRORS else 'delivery_collection_failed')
                    summary.update(status='failed', reason='delivery_collection_failed_at_'+name)
                    code = 2; break
                row['delivery'] = name+'/delivery.zip'
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
