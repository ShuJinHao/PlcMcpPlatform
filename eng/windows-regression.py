"""Windows validation preflight and serial engineering regression, with local evidence.

Without --execute only files, identities and runtime prerequisites are inspected.
With --execute a dedicated ValidationHost is owned for this run, and every job is
recorded. No online PLC operation or capability promotion is performed.
"""
import argparse
import datetime
import importlib.util
import json
import os
from pathlib import Path
import platform
import secrets
import socket
import subprocess
import sys
import time
import uuid
from bundle import sha256, verify_bundle
from doctor import inspect
from mcp_client import McpClient
from start import prepare

ROOT = Path(__file__).resolve().parents[1]


def load_helper(name, filename):
    spec = importlib.util.spec_from_file_location(name, ROOT/'eng'/filename)
    module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
    return module


def atomic_json(path, value):
    path = Path(path)
    temporary = path.with_suffix(path.suffix+'.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    temporary.replace(path)


def file_identity(path):
    path = Path(path).resolve()
    if not path.is_file() or path.is_symlink(): raise ValueError('Identity file must exist: '+str(path))
    return dict(path=str(path), byteLength=path.stat().st_size, sha256=sha256(path))


def windows_versions(executable, engine):
    # Pass paths as environment data, never interpolate them into PowerShell code.
    command = "$ErrorActionPreference='Stop'; $f=[System.Diagnostics.FileVersionInfo]::GetVersionInfo($env:PLCMCP_INSPECT_EXE); "
    command += "$e=[System.Reflection.AssemblyName]::GetAssemblyName($env:PLCMCP_INSPECT_ENGINE); "
    command += "@{exeFileVersion=$f.FileVersion;exeProductVersion=$f.ProductVersion;engineAssemblyVersion=$e.Version.ToString()} | ConvertTo-Json -Compress"
    env = dict(os.environ, PLCMCP_INSPECT_EXE=str(executable), PLCMCP_INSPECT_ENGINE=str(engine))
    return json.loads(subprocess.check_output(['powershell.exe', '-NoProfile', '-NonInteractive', '-Command', command],
                                             env=env, text=True, timeout=30, encoding='utf-8-sig'))


def missing_values(value, path=''):
    if isinstance(value, dict):
        return [p for key, child in value.items() for p in missing_values(child, path+'/'+key)]
    if isinstance(value, list):
        return [p for i, child in enumerate(value) for p in missing_values(child, path+'/'+str(i))]
    return [path] if value is None or isinstance(value, str) and (not value or 'REPLACE_' in value) else []


def discover_ids(objects, variables):
    """Only select unique, typed identities. Ambiguity is reported, not guessed."""
    values = dict(variables); decisions = {}
    by_id = {o['id']:o for o in objects}
    def ancestors(identity):
        seen = set()
        while identity in by_id:
            if identity in seen: raise ValueError('Observed object tree contains a parent cycle.')
            seen.add(identity)
            identity = by_id[identity].get('parentId')
            if identity is not None: yield identity
    def assign(key, candidates):
        candidates = [row for row in candidates if row.get('id')]
        current = values.get(key)
        if current and not str(current).startswith('REPLACE_'):
            if current not in {row['id'] for row in candidates}: raise ValueError('Configured '+key+' does not match the observed object tree.')
            decisions[key] = dict(source='configured_and_observed', value=current)
        elif len(candidates) == 1:
            values[key] = candidates[0]['id']; decisions[key] = dict(source='unique_observed_object', value=candidates[0]['id'])
        else: decisions[key] = dict(source='manual_selection_required', candidates=candidates)
    assign('applicationId', [o for o in objects if o.get('kind') == 'application'])
    app = values.get('applicationId')
    # A plain tree labels textual POUs as object on the real worker. Only a later
    # source read can establish that a same-named object is a PROGRAM.
    assign('programId', [o for o in objects if o.get('kind') in ('pou', 'object') and o.get('name') == values.get('programName') and app in ancestors(o['id'])])
    device = next((by_id[i] for i in ancestors(app) if i in by_id and by_id[i].get('kind') == 'device'), None)
    assign('controllerId', [device] if device else [])
    assign('libraryManagerId', [o for o in objects if o.get('kind') == 'library_manager' and o.get('parentId') == app])
    return values, decisions


def preflight(args, report):
    bundle = Path(args.bundle).resolve()
    manifest = verify_bundle(bundle)
    if manifest['mode'] != 'validation' or manifest['sourceDirty']: raise ValueError('Use a clean, source-bound validation bundle.')
    if sys.platform != 'win32': raise ValueError('This entry requires Windows with the target InoProShop installation.')
    settings = json.loads(Path(args.settings).read_text(encoding='utf-8-sig'))
    required = ('dataDirectory', 'workspaceRoot', 'executablePath', 'engineAssemblyPath', 'profilePath', 'profile', 'variablesPath')
    if any(not settings.get(k) or 'REPLACE_' in settings[k] for k in required): raise ValueError('Complete all paths and the exact Profile in the settings file.')
    for name in required:
        if name != 'profile' and not Path(settings[name]).is_absolute(): raise ValueError(name+' must be an absolute path.')
    env = dict(os.environ, PlcMcp__ApiKey=os.environ.get('PlcMcp__ApiKey') or secrets.token_hex(32),
               PlcMcp__ValidationProfilePath=settings['profilePath'], PlcMcp__Inovance__ExecutablePath=settings['executablePath'],
               PlcMcp__Inovance__EngineAssemblyPath=settings['engineAssemblyPath'], PlcMcp__Inovance__Profile=settings['profile'])
    _, command, env = prepare(bundle, settings['dataDirectory'], settings['workspaceRoot'], args.dotnet, env)
    profile = json.loads(Path(settings['profilePath']).read_text(encoding='utf-8-sig'))
    identities = dict(executable=file_identity(settings['executablePath']), engine=file_identity(settings['engineAssemblyPath']),
                      profile=file_identity(settings['profilePath']))
    versions = windows_versions(settings['executablePath'], settings['engineAssemblyPath'])
    if versions['engineAssemblyVersion'] != profile['engineVersion']: raise ValueError('Engine assembly version differs from the validation Profile.')
    # Product/file versions are recorded separately: neither is silently substituted for a product version.
    variables = json.loads(Path(settings['variablesPath']).read_text(encoding='utf-8-sig'))
    report.update(sourceCommit=manifest['sourceCommit'], sourceTree=manifest['sourceTree'], bundleVersion=manifest['version'],
                  environment=dict(platform=platform.platform(), architecture=platform.machine(), python=platform.python_version(),
                                   versions=versions, identities=identities, validationIdentity=profile),
                  manualFields=missing_values(variables))
    with socket.socket() as probe:
        if probe.connect_ex(('127.0.0.1', 5088)) == 0: raise ValueError('Port 5088 is occupied; this entry never stops another host.')
    report['preflightPassed'] = True
    return settings, variables, command, env


def execute(args, report, settings, variables, command, env, output):
    client = None
    log_path = output/'host.log'
    with log_path.open('xb') as log:
        child = subprocess.Popen(command, cwd=Path(args.bundle)/'bin', env=env, stdin=subprocess.DEVNULL, stdout=log, stderr=log)
        try:
            deadline = time.monotonic()+45
            while time.monotonic() < deadline:
                if child.poll() is not None: raise ValueError('ValidationHost exited; inspect host.log.')
                try:
                    client = McpClient('http://127.0.0.1:5088/mcp', env['PlcMcp__ApiKey'])
                    report['runtime'] = inspect(client, 'validation'); break
                except OSError: time.sleep(0.25)
            else: raise ValueError('ValidationHost readiness timed out.')
            if not report['runtime']['succeeded']: raise ValueError('Runtime preflight did not pass.')
            templates = client.call('plc_templates_list', {})
            atomic_json(output/'templates.json', templates)
            template_id = variables.get('templateId', '')
            template_rows = templates.get('value', [])
            if not template_id or template_id.startswith('REPLACE_'):
                if len(template_rows) != 1: raise ValueError('Select templateId from templates.json.')
                template_id = variables['templateId'] = template_rows[0]['id']
            workspace = 'preflight-'+uuid.uuid4().hex[:16]
            evidence = []
            def job(tool, arguments):
                arguments = dict(arguments, idempotencyKey=uuid.uuid4().hex)
                reply = client.call(tool, arguments)
                value = client.wait(reply['job']['jobId'], args.poll_timeout)
                evidence.append(dict(tool=tool, arguments=arguments, result=value))
                atomic_json(output/'discovery.json', evidence)
                if value['status'] != 'Succeeded': raise ValueError('Discovery stopped at '+tool+': '+value['status']+'; no retry performed.')
                return value['result']
            created = job('plc_project_create', dict(workspaceName=workspace, templateId=template_id))
            tree = job('plc_project_get_structure', dict(projectPath=created['projectPath'], maxObjects=2000))
            if tree.get('truncated'): raise ValueError('Object tree is truncated; select a smaller regression template.')
            variables, decisions = discover_ids(tree['objects'], variables)
            report['objectDiscovery'] = decisions
            program_id = variables.get('programId', '')
            if program_id and not program_id.startswith('REPLACE_'):
                import re
                program = job('plc_pou_read', dict(projectPath=created['projectPath'], objectId=program_id))
                if not re.match(r'\s*PROGRAM\s+'+re.escape(variables['programName'])+r'\b', program.get('declaration', ''), re.I):
                    raise ValueError('Selected program is not the expected PROGRAM declaration.')
            job('plc_device_get_tree', dict(projectPath=created['projectPath'], maxObjects=2000))
            installed = job('plc_library_list_installed', dict(projectPath=created['projectPath'], maxObjects=2000))
            report['manualFields'] = missing_values(variables)
            atomic_json(output/'resolved-variables.json', variables)
            if report['manualFields']: raise ValueError('Complete the remaining fields in resolved-variables.json and select that file in settings.')
            library_data = installed['configuration']['data']
            if library_data.get('truncated'): raise ValueError('Installed library discovery is incomplete; inspect discovery.json.')
            if variables['libraryName'] not in {row['name'] for row in library_data['libraries']}:
                raise ValueError('Selected regression library is not installed; choose its exact observed display name.')
            compilation = job('plc_project_compile', dict(projectPath=created['projectPath'], applicationId=variables['applicationId']))
            if compilation.get('succeeded') is not True: raise ValueError('Template compilation failed; inspect discovery.json diagnostics and installed dependencies.')
            report['templateCompilePassed'] = True
            suites = load_helper('handoff_suites', 'run-regressions.py')
            for name, filename in suites.SUITES.items():
                scenario = json.loads((ROOT/'config/regression'/filename).read_text(encoding='utf-8'))
                result = suites.regression.run(client, scenario, dict(variables, runId=uuid.uuid4().hex[:16]), 'real', args.poll_timeout)
                atomic_json(output/(name+'.json'), result)
                report['suites'].append(dict(suite=name, succeeded=result['succeeded'], report=name+'.json',
                                            failedCase=next((c['id'] for c in result['cases'] if not c['succeeded']), None)))
                atomic_json(output/'summary.json', report)
                if not result['succeeded']: raise ValueError('Regression stopped in '+name+'; inspect its report before repair or rerun.')
            report.update(status='passed', realIdeScenarioPassed=True)
        finally:
            # Terminate only the process created above. Never kill by image name or port.
            if child.poll() is None:
                child.terminate()
                try: child.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    report['hostStopConfirmed'] = False
                    raise ValueError('Host stop not confirmed; inspect unresolved jobs before any restart.')
            report['hostStopConfirmed'] = True


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--bundle', required=True); parser.add_argument('--settings', required=True)
    parser.add_argument('--report-directory', required=True); parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--execute', action='store_true'); parser.add_argument('--poll-timeout', type=int, default=660)
    args = parser.parse_args()
    output = Path(args.report_directory).resolve()
    if output.exists(): parser.error('Choose a new report directory; evidence is never overwritten.')
    output.mkdir(parents=True)
    report = dict(schemaVersion=1, status='not_run', startedUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),
                  preflightPassed=False, suites=[], realIdeScenarioPassed=False, engineeringReady=False,
                  compatibilityCertified=False, plcRuntimeValidated=False)
    result = 1
    try:
        settings, variables, command, env = preflight(args, report)
        if args.execute: execute(args, report, settings, variables, command, env, output)
        else: report.update(status='preflight_only', reason='execution_requires_execute')
        result = 0
    except Exception as error:
        report.update(status='failed', error=str(error))
    report['finishedUtc'] = datetime.datetime.now(datetime.timezone.utc).isoformat()
    # Environment secrets are never serialized. Evidence may contain local engineering data.
    atomic_json(output/'summary.json', report)
    print(json.dumps(dict(status=report['status'], report=str(output/'summary.json'), compatibilityCertified=False)))
    return result


if __name__ == '__main__': sys.exit(main())
