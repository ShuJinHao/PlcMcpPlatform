"""Local POSIX development entry. Owns only its fixture host; never an installed IDE.

Provider credentials stay outside the repository. Native OpenCode project configuration
is written to an isolated, ignored client directory; existing user configuration is retained.
"""
import argparse
import contextlib
import hashlib
import json
import os
from pathlib import Path
import secrets
import shutil
import signal
import socket
import stat
import subprocess
import sys
import time
import uuid

ROOT = Path(__file__).resolve().parents[1]
LOCAL = ROOT / '.plcmcp'
DEV = LOCAL / 'dev'
CLIENT = LOCAL / 'client'
HOST = ROOT / 'tests/PlcMcp.TestHost/bin/Release/net8.0/PlcMcp.TestHost.dll'
URL = 'http://127.0.0.1:5088/mcp'


def private_write(path, text):
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, 'w', encoding='utf-8') as stream:
        stream.write(text)


@contextlib.contextmanager
def ownership_lock():
    import fcntl
    DEV.mkdir(parents=True, exist_ok=True, mode=0o700)
    with (DEV / 'launcher.lock').open('a') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        yield


def dotnet_environment(executable):
    executable = str(Path(shutil.which(executable) or executable).resolve())
    env = dict(os.environ, DOTNET_ROOT=str(Path(executable).parent), DOTNET_HOST_PATH=executable,
               DOTNET_CLI_HOME=str(LOCAL / 'dotnet-home'), DOTNET_NOLOGO='1',
               DOTNET_CLI_TELEMETRY_OPTOUT='1', DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1',
               NUGET_PACKAGES=str(LOCAL / 'nuget'))
    env['PATH'] = str(Path(executable).parent) + os.pathsep + env.get('PATH', '')
    return executable, env


def sdk(args):
    candidate = args.dotnet or str(LOCAL / 'dotnet/dotnet')
    if not Path(candidate).is_file() and not shutil.which(candidate): candidate = 'dotnet'
    executable, env = dotnet_environment(candidate)
    versions = subprocess.check_output([executable, '--list-sdks'], env=env, text=True, timeout=30)
    if not any(line.startswith('8.0.') for line in versions.splitlines()):
        raise ValueError('Install the .NET 8 SDK or pass --dotnet; see docs/26-mac-windows-handoff.md.')
    return executable, env


def setup(args, build=True):
    executable, env = sdk(args)
    DEV.mkdir(parents=True, exist_ok=True, mode=0o700)
    key_path = DEV / 'mcp-key'
    if not key_path.exists(): private_write(key_path, secrets.token_hex(32) + '\n')
    if not (DEV / 'workspaces/templates/sample/template.json').exists():
        subprocess.run([sys.executable, str(ROOT/'eng/init-fixture.py'), '--workspace-root', str(DEV/'workspaces')], check=True)
    source = DEV / 'workspaces/imports/existing/project.fixture.json'
    if not source.exists():
        private_write(source, (DEV/'workspaces/templates/sample/project.fixture.json').read_text(encoding='utf-8'))
    CLIENT.mkdir(parents=True, exist_ok=True)
    config = json.loads((ROOT/'config/opencode.example.json').read_text(encoding='utf-8'))
    config.update(json.loads((ROOT/'config/deepseek.example.json').read_text(encoding='utf-8')))
    config.update(model='deepseek/deepseek-flash', share='disabled')
    # Refuse to overwrite edits to this generated project configuration.
    config_path = CLIENT / 'opencode.json'
    if not config_path.exists(): private_write(config_path, json.dumps(config, ensure_ascii=False, indent=2)+'\n')
    instructions = CLIENT / 'AGENTS.md'
    if not instructions.exists():
        private_write(instructions, '# PLC development workspace\n\nUse the plc MCP server for PLC programming. '
                      'This host is fixture/simulated: no InoProShop or PLC is connected. '
                      'Read status and capabilities before editing. Use template sample and obtain IDs from tools. '
                      'Use preview, revision checks, final compilation and readback before delivery. '
                      'Do not retry OutcomeUnknown writes or claim vendor compatibility from simulation.\n')
    if build:
        subprocess.run([executable, 'restore', 'PlcMcpPlatform.sln', '--locked-mode'], cwd=ROOT, env=env, check=True)
        subprocess.run([executable, 'build', 'PlcMcpPlatform.sln', '-c', 'Release', '--no-restore'], cwd=ROOT, env=env, check=True)
    if not HOST.is_file(): raise ValueError('Run setup to build the fixture host first.')
    env.update(PlcMcp__ApiKey=read_private(key_path), DOTNET_ENVIRONMENT='Development',
               PlcMcp__DataDirectory=str(DEV/'data'), PlcMcp__WorkspaceRoot=str(DEV/'workspaces'))
    return executable, env


def read_private(path):
    if path.is_symlink() or not path.is_file(): raise ValueError('Credential path must be a regular private file.')
    if path.stat().st_mode & (stat.S_IRWXG | stat.S_IRWXO): raise ValueError('Credential file must have mode 600.')
    value = path.read_text(encoding='utf-8').strip()
    if not value or '\n' in value: raise ValueError('Credential file must contain a single nonempty line.')
    return value


def process_identity(pid):
    result = subprocess.run(['ps', '-ww', '-p', str(pid), '-o', 'lstart=', '-o', 'command='],
                            text=True, stdout=subprocess.PIPE, stderr=subprocess.DEVNULL, timeout=10)
    return result.stdout.strip() if result.returncode == 0 else ''


def owned_state():
    path = DEV / 'host.json'
    if not path.exists(): return None
    state = json.loads(path.read_text(encoding='utf-8'))
    identity = process_identity(int(state['pid']))
    if not identity: return None
    if '--PlcMcpDevOwner='+state['owner'] not in identity or hashlib.sha256(identity.encode()).hexdigest() != state['identitySha256']:
        raise ValueError('Saved PID identity no longer matches; refusing to stop or reuse that process.')
    return state


def inspect():
    from doctor import inspect as diagnose
    from mcp_client import McpClient
    return diagnose(McpClient(URL, read_private(DEV/'mcp-key')), 'fixture')


def start(args):
    if owned_state():
        return dict(status='running', **inspect())
    with socket.socket() as probe:
        if probe.connect_ex(('127.0.0.1', 5088)) == 0:
            raise ValueError('Port 5088 is occupied by another process; it will not be stopped.')
    executable, env = setup(args, build=not args.no_build)
    owner = uuid.uuid4().hex
    with (DEV/'host.log').open('ab') as log:
        child = subprocess.Popen([executable, str(HOST), '--PlcMcpDevOwner='+owner], cwd=HOST.parent, env=env,
                                 stdin=subprocess.DEVNULL, stdout=log, stderr=log, start_new_session=True)
    try:
        identity = process_identity(child.pid)
        if not identity or '--PlcMcpDevOwner='+owner not in identity: raise ValueError('Host process could not be identified.')
        state = dict(pid=child.pid, owner=owner, identitySha256=hashlib.sha256(identity.encode()).hexdigest())
        (DEV/'host.json').write_text(json.dumps(state)+'\n', encoding='utf-8')
        deadline = time.monotonic()+30
        while time.monotonic() < deadline:
            if child.poll() is not None: raise ValueError('Fixture host exited; inspect .plcmcp/dev/host.log.')
            try: return dict(status='started', **inspect())
            except (OSError, ValueError): time.sleep(0.2)
        raise ValueError('Host readiness timed out; inspect .plcmcp/dev/host.log.')
    except BaseException:
        # This Popen still identifies our own child. No global PID/name/port killing.
        if child.poll() is None:
            child.terminate()
            child.wait(timeout=15)
        raise


def stop():
    state = owned_state()
    if not state: return dict(status='stopped', evidenceKind='simulated')
    os.kill(state['pid'], signal.SIGTERM)
    deadline = time.monotonic()+15
    while time.monotonic() < deadline:
        if not process_identity(state['pid']):
            (DEV/'host.json').unlink(missing_ok=True)
            return dict(status='stopped', evidenceKind='simulated')
        time.sleep(0.1)
    raise ValueError('Stop not confirmed; no force kill or job retry was performed. Inspect the host log and jobs.')


def provider_environment(env):
    env = dict(env)
    if not env.get('DEEPSEEK_API_KEY'):
        path = Path(os.environ.get('PLCMCP_DEEPSEEK_KEY_FILE', '~/.config/plcmcp/deepseek-api-key')).expanduser()
        if path.exists(): env['DEEPSEEK_API_KEY'] = read_private(path)
        else:
            auth = Path(os.environ.get('XDG_DATA_HOME', str(Path.home()/'.local/share')))/'opencode/auth.json'
            if auth.is_file() and not auth.is_symlink():
                record = json.loads(auth.read_text(encoding='utf-8')).get('deepseek', {})
                if record.get('type') == 'api' and isinstance(record.get('key'), str): env['DEEPSEEK_API_KEY'] = record['key']
    return env


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('action', choices=['setup', 'start', 'status', 'stop', 'restart', 'opencode', 'model'])
    parser.add_argument('--dotnet'); parser.add_argument('--no-build', action='store_true')
    parser.add_argument('--opencode', default='opencode'); parser.add_argument('--report')
    parser.add_argument('--timeout', type=int, default=900)
    parser.add_argument('--scenario', choices=['repair', 'graphics', 'members', 'configuration', 'incremental'], default='repair')
    args = parser.parse_args()
    if os.name != 'posix': parser.error('Use eng/windows-regression.py for the Windows handoff.')
    try:
        with ownership_lock():
            if args.action == 'stop': result = stop()
            elif args.action == 'status': result = dict(status='running', **inspect()) if owned_state() else dict(status='stopped', evidenceKind='simulated')
            elif args.action == 'setup':
                setup(args, build=not args.no_build); result = dict(status='prepared', evidenceKind='simulated', clientDirectory=str(CLIENT))
            else:
                if args.action == 'restart': stop()
                result = start(args)
        print(json.dumps(result, ensure_ascii=False), flush=True)
        if args.action in ('opencode', 'model'):
            _, env = setup(args, build=False)
            env = provider_environment(env)
            if args.action == 'opencode':
                return subprocess.call([args.opencode, str(CLIENT)], env=env)
            report = args.report or str(ROOT/'artifacts/mac'/('model-'+time.strftime('%Y%m%d-%H%M%S')+'.json'))
            command = [sys.executable, str(ROOT/'eng/model-regression.py'), '--execute', '--model', 'deepseek/deepseek-flash',
                       '--opencode', args.opencode, '--variables', str(ROOT/'config/regression/fixture-variables.json'),
                       '--provider-config', str(ROOT/'config/deepseek.example.json'), '--provider-env', 'DEEPSEEK_API_KEY',
                       '--report', report, '--timeout', str(args.timeout), '--scenario', args.scenario]
            return subprocess.call(command, cwd=ROOT, env=env)
        return 0
    except (OSError, ValueError, subprocess.SubprocessError) as error:
        print(str(error), file=sys.stderr); return 1


if __name__ == '__main__': sys.exit(main())
