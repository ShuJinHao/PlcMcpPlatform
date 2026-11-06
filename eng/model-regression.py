"""Optional real-model regression through the stock OpenCode CLI and standard MCP.
No model provider, session store or client adapter is added to the PLC server.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
import uuid
import zipfile
from mcp_client import McpClient
from transfer import download_delivery

PINNED_OPENCODE = '1.19.5'


def objects(value):
    if isinstance(value, dict):
        yield value
        for child in value.values(): yield from objects(child)
    elif isinstance(value, list):
        for child in value: yield from objects(child)


def decode_output(value):
    if isinstance(value, str):
        try: return decode_output(json.loads(value))
        except (ValueError, TypeError): return None
    if isinstance(value, list):
        for block in value:
            decoded = decode_output(block.get('text')) if isinstance(block, dict) else None
            if decoded is not None: return decoded
        return None
    if isinstance(value, dict) and 'structuredContent' in value: return value['structuredContent']
    if isinstance(value, dict) and 'content' in value:
        for block in value['content']:
            decoded = decode_output(block.get('text')) if isinstance(block, dict) else None
            if decoded is not None: return decoded
    return value if isinstance(value, dict) else None


def calls_from_events(raw, allowed_tools):
    calls = []; ids = set(); session = None
    for line in raw.splitlines():
        if not line.strip(): continue
        event = json.loads(line)
        if event.get('type') == 'error': raise ValueError('model_cli_error')
        if event.get('type') != 'tool_use': continue
        part = event.get('part', {}); state = part.get('state', {})
        call_id = part.get('callID'); name = part.get('tool', '')
        if name.startswith('plc_plc_'): name = name[4:]
        if name not in allowed_tools: raise ValueError('unexpected_non_plc_tool')
        if not call_id or call_id in ids: raise ValueError('duplicate_or_missing_call_id')
        ids.add(call_id)
        current = event.get('sessionID')
        if not current or session is not None and current != session: raise ValueError('mixed_model_sessions')
        session = current
        if state.get('status') not in ('completed', 'error'): raise ValueError('incomplete_tool_event')
        calls.append(dict(name=name, input=state.get('input', {}), output=decode_output(state.get('output')), status=state['status']))
    if not calls: raise ValueError('no_observed_mcp_calls')
    return calls


def prove(calls, jobs, head, workspace_name, mode):
    """Use jobs read back independently from the server, never the model's final narrative."""
    for call in calls:
        if call['input'].get('workspaceName', workspace_name) != workspace_name: raise ValueError('workspace_scope_mismatch')
    if any(j['status'] in ('Queued', 'Running', 'CancelRequested', 'OutcomeUnknown') for j in jobs): raise ValueError('job_outcome_unresolved')
    if any(j.get('executionMode') != mode for j in jobs): raise ValueError('job_mode_mismatch')
    success = [j for j in jobs if j['status'] == 'Succeeded']
    batches = [j for j in success if j['operation'] == 'project.batch' and (j.get('result') or {}).get('published') is True]
    failed = [j for j in jobs if j['status'] == 'Failed' and j['operation'] == 'project.batch'
              and j.get('errorCode') == 'compile_failed' and (j.get('result') or {}).get('published') is False]
    if not any(j['operation'] == 'project.create' for j in success) or len(batches) < 2 or not failed:
        raise ValueError('generation_edit_or_compile_repair_missing')
    observed_failure = False
    failed_ids = {j['jobId'] for j in failed}
    for index, call in enumerate(calls):
        if call['name'] not in ('plc_job_get', 'plc_job_get_result') or call['input'].get('jobId') not in failed_ids: continue
        if not any(row.get('status') == 'Failed' and row.get('errorCode') == 'compile_failed' for row in objects(call['output'])): continue
        if any(later['name'] == 'plc_project_batch_apply' for later in calls[index+1:]): observed_failure = True
    if not observed_failure: raise ValueError('model_did_not_observe_failure_before_repair')
    revisions = {j['result']['revision'] for j in batches}
    if head['revision'] not in revisions: raise ValueError('final_revision_not_from_observed_batch')
    project_file = str(head['projectPath']).replace('\\', '/').split('/')[-1]
    project_sha = next((f['sha256'] for f in head['files'] if f['path'] == project_file), None)
    sources = [j['result'] for j in success if j['operation'] == 'pou.read' and (j.get('result') or {}).get('revision') == project_sha]
    corrected = [s for s in sources if re.search(r'\bPROGRAM\s+P10Main\b', s.get('declaration', ''), re.I)
                 and re.fullmatch(r'\s*Counter\s*:=\s*Counter\s*\+\s*2\s*;\s*', s.get('implementation', ''), re.I)]
    if not corrected or len({s.get('objectId') for s in corrected}) != 1: raise ValueError('final_source_readback_missing')
    packages = [row['artifact'] for job in success if job['operation'] == 'project.package'
                for row in objects(job.get('result')) if isinstance(row.get('artifact'), dict)]
    matching = [a for a in packages if a.get('workspaceName') == workspace_name and a.get('revision') == head['revision'] and a.get('executionMode') == mode]
    if not matching or len({a.get('artifactId') for a in matching}) != 1: raise ValueError('exact_final_delivery_missing')
    return matching[0], corrected[0]['objectId']


def prompt(workspace, template, app, mode):
    invalid = 'Counter := FIXTURE_ERROR;' if mode == 'fixture' else 'Counter := ;'
    return f'''Use only the plc MCP server. This is an explicitly authorized isolated regression workspace: {workspace}.
Read status, capabilities and template {template}; create ONLY this new workspace. Target Application: {app}.
Generate an ST Program P10Main with a local DINT Counter that increments by 1 each invocation.
Create a 20 ms cyclic task P10Task and bind P10Main, creating a Task Configuration only if needed.
Use batch preview/apply for program creation and task binding; publish only after final compilation succeeds.
Then submit one previewed batch updating P10Main's implementation to exactly `{invalid}` to exercise diagnostic handling.
Wait for the deterministic compile_failed result, inspect diagnostics and verify no revision was published.
In fixture this is an intentional simulated compiler sentinel, never evidence of a vendor compiler.
After observing that failure, generate a corrected batch with `Counter := Counter + 2;`, preserving the declaration and task binding.
Preview against the still-valid head with a NEW idempotency key for changed content; apply and wait for successful publication.
Read the final program using plc_pou_read, recheck tasks, and create a delivery for the exact final m2 revision.
Poll every submitted job to terminal status; return the delivery ID and SHA256. Do not print ZIP/base64 content.
Obtain GUIDs and paths from tools, use @stepId only for batch references, never invent object identities.
If a job is OutcomeUnknown or cancellation is unconfirmed, stop without retry, reconcile or unlocking.
Use revisions from tools; project file hashes and managed m2 revisions are different scopes.
All writes to this isolated workspace are authorized for this run. Never change another workspace, use shell tools or invoke online PLC operations.
Label results accurately as {mode}; a fixture result is simulated. Compilation is not PLC runtime validation.
'''


def environment(directory, provider_names, key, config):
    allowed = ('PATH', 'SYSTEMROOT', 'WINDIR', 'TEMP', 'TMP', 'LANG', 'LC_ALL', 'COMSPEC', 'PATHEXT')
    env = {k: v for k, v in os.environ.items() if k.upper() in allowed}
    for name in provider_names:
        if not re.fullmatch(r'[A-Z][A-Z0-9_]{0,55}_(?:API_KEY|AUTH_TOKEN)', name): raise ValueError('provider_env_name_invalid')
        if not os.environ.get(name): raise ValueError('provider_env_missing')
        env[name] = os.environ[name]
    env.update(XDG_CONFIG_HOME=str(directory/'config'), XDG_DATA_HOME=str(directory/'data'),
               XDG_STATE_HOME=str(directory/'state'), XDG_CACHE_HOME=str(directory/'cache'),
               OPENCODE_CONFIG_CONTENT=json.dumps(config), PlcMcp__ApiKey=key,
               OPENCODE_DISABLE_AUTOUPDATE='true', OPENCODE_DISABLE_MODELS_FETCH='true', OPENCODE_DISABLE_LSP_DOWNLOAD='true',
               OPENCODE_DISABLE_DEFAULT_PLUGINS='true', OPENCODE_DISABLE_CLAUDE_CODE='true', OPENCODE_DISABLE_PROJECT_CONFIG='true',
               OPENCODE_PURE='true', NO_COLOR='1')
    return env


def run(args, report):
    if not args.execute or not args.model:
        report.update(status='skipped', reason='explicit_execute_and_model_required'); return 3
    if not re.fullmatch(r'[^\s/]+/[^\s]+', args.model) or not 30 <= args.timeout <= 3600: raise ValueError('model_or_timeout_invalid')
    key = os.environ.get('PLCMCP_API_KEY') or os.environ.get('PlcMcp__ApiKey')
    if not key: raise ValueError('mcp_key_missing')
    if not args.variables: raise ValueError('variables_required')
    variables = json.loads(Path(args.variables).read_text(encoding='utf-8'))
    template, app = variables['templateId'], str(uuid.UUID(variables['applicationId']))
    if not re.fullmatch('[a-z][a-z0-9-]{0,47}', template): raise ValueError('invalid_template')
    client = McpClient(args.url, key)
    runtime = client.call('plc_runtime_get_status', {})
    if runtime['executionMode'] != args.expect_mode or runtime.get('schedulerBlocked'): raise ValueError('runtime_mode_or_execution_blocked')
    if args.expect_mode == 'real' and not runtime.get('validation'): raise ValueError('isolated_validation_host_required')
    allowed_tools = {t['name'] for t in client.rpc('tools/list')['tools']}
    required = {'plc_project_create', 'plc_project_batch_apply', 'plc_project_batch_preview', 'plc_pou_read', 'plc_delivery_create'}
    if not required <= allowed_tools: raise ValueError('required_tools_unavailable')
    report['connectionVerified'] = True
    workspace = 'model-' + uuid.uuid4().hex[:16]
    report.update(workspaceName=workspace, model=args.model, executionMode=args.expect_mode)
    config = json.loads((Path(__file__).resolve().parents[1]/'config/opencode.example.json').read_text())
    config['mcp']['plc']['url'] = args.url
    config.update(permission={'*': 'deny', 'plc_*': 'allow'}, share='disabled', autoupdate=False)
    if args.provider_config:
        path = Path(args.provider_config)
        if path.stat().st_size > 65536: raise ValueError('provider_config_too_large')
        custom = json.loads(path.read_text(encoding='utf-8'))
        if set(custom) != {'provider'} or not isinstance(custom['provider'], dict): raise ValueError('provider_only_config_required')
        for row in objects(custom):
            for name, value in row.items():
                if name.lower() in ('apikey', 'authorization', 'token', 'password') and (not isinstance(value, str) or not re.fullmatch(r'(?:Bearer )?\{env:[A-Z0-9_]+\}', value)):
                    raise ValueError('provider_secrets_require_env_reference')
        config.update(custom)
    with tempfile.TemporaryDirectory(prefix='plcmcp-model-') as tmp:
        directory = Path(tmp); env = environment(directory, args.provider_env, key, config)
        version = subprocess.check_output([args.opencode, '--version'], cwd=directory, env=env, timeout=30, text=True).strip()
        report['openCodeVersion'] = version
        if version != PINNED_OPENCODE: raise ValueError('opencode_version_not_pinned')
        text = prompt(workspace, template, app, args.expect_mode)
        with (directory/'events').open('w+b') as output, (directory/'stderr').open('w+b') as errors:
            process = subprocess.Popen([args.opencode, 'run', '--format', 'json', '--model', args.model, text],
                                       cwd=directory, env=env, stdout=output, stderr=errors)
            deadline = time.monotonic()+args.timeout
            try:
                while process.poll() is None:
                    if time.monotonic() >= deadline: raise TimeoutError('model_timeout_jobs_may_continue')
                    if output.tell() > 32*1024*1024 or errors.tell() > 2*1024*1024: raise ValueError('model_output_limit')
                    time.sleep(0.1)
                output.seek(0); raw = output.read(32*1024*1024+1)
                if len(raw) > 32*1024*1024: raise ValueError('model_output_limit')
                if process.returncode != 0: raise ValueError('model_cli_failed')
            finally:
                if process.poll() is None: process.kill(); process.wait(timeout=10)
        report['eventStreamSha256'] = hashlib.sha256(raw).hexdigest()
        calls = calls_from_events(raw.decode('utf-8'), allowed_tools)
        ids = {row['jobId'] for call in calls for row in objects(call['output']) if isinstance(row.get('jobId'), str)}
        ids.update(call['input']['jobId'] for call in calls if call['name'] in ('plc_job_get', 'plc_job_get_result') and 'jobId' in call['input'])
        if len(ids) > 128: raise ValueError('too_many_jobs')
        jobs = [client.call('plc_job_get', dict(jobId=str(uuid.UUID(i))))['job'] for i in sorted(ids)]
        head = client.call('plc_workspace_get', dict(workspaceName=workspace))['value']
        artifact, source_id = prove(calls, jobs, head, workspace, args.expect_mode)
        with tempfile.TemporaryDirectory(prefix='plcmcp-model-delivery-') as delivery:
            package = Path(delivery)/'result.zip'
            download_delivery(client, artifact['artifactId'], package, artifact['sha256'])
            with zipfile.ZipFile(package) as archive:
                for name in ('manifest.json', 'reports/compile.json', 'reports/tasks.json'):
                    if archive.getinfo(name).file_size > 4*1024*1024: raise ValueError('delivery_report_limit')
                manifest = json.loads(archive.read('manifest.json'))
                if manifest['revision'] != head['revision'] or manifest['workspaceName'] != workspace or manifest['executionMode'] != args.expect_mode:
                    raise ValueError('delivery_manifest_mismatch')
                compile_report = json.loads(archive.read('reports/compile.json'))
                tasks = json.loads(archive.read('reports/tasks.json'))
                if not any(row.get('succeeded') is True for row in objects(compile_report)): raise ValueError('delivery_compilation_missing')
                if not any(row.get('pouName') == 'P10Main' and row.get('pouId') == source_id for row in objects(tasks)): raise ValueError('task_binding_missing')
        report.update(status='passed', llmGenerationVerified=True, realIdeScenarioPassed=args.expect_mode == 'real',
                      evidenceKind='simulated' if args.expect_mode == 'fixture' else 'unverified',
                      toolCalls=len(calls), jobIds=sorted(ids), finalRevision=head['revision'], deliverySha256=artifact['sha256'])
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--report', required=True); parser.add_argument('--execute', action='store_true')
    parser.add_argument('--model'); parser.add_argument('--opencode', default='opencode')
    parser.add_argument('--provider-env', action='append', default=[]); parser.add_argument('--provider-config')
    parser.add_argument('--variables'); parser.add_argument('--timeout', type=int, default=900)
    parser.add_argument('--expect-mode', choices=['fixture', 'real'], default='fixture')
    parser.add_argument('--url', default='http://127.0.0.1:5088/mcp'); args = parser.parse_args()
    destination = Path(args.report).resolve()
    if destination.exists(): parser.error('Choose a new report path.')
    report = dict(schemaVersion=1, status='not_run', connectionVerified=False, llmGenerationVerified=False,
                  realIdeScenarioPassed=False, plcRuntimeValidated=False, engineeringReady=False)
    try: result = run(args, report)
    except Exception as error:
        # Never emit provider stderr, credentials, prompts or PLC source into CI output.
        code = str(error) if isinstance(error, (ValueError, TimeoutError)) and re.fullmatch('[a-z_]+', str(error)) else type(error).__name__
        report.update(status='failed', errorCode=code); result=2
    destination.parent.mkdir(parents=True, exist_ok=True)
    with destination.open('x', encoding='utf-8') as output: json.dump(report, output, ensure_ascii=False, indent=2); output.write('\n')
    print(json.dumps(report, ensure_ascii=False)); return result


if __name__ == '__main__': raise SystemExit(main())
