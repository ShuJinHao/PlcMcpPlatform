"""Validate a release and start its foreground host; never launches an IDE itself."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
from bundle import verify_bundle


def prepare(bundle, data, workspace, dotnet='dotnet', environment=None):
    root = Path(bundle).resolve(); manifest = verify_bundle(root); env = dict(os.environ if environment is None else environment)
    mode = manifest['mode']
    if mode == 'validation' and sys.platform != 'win32': raise ValueError('ValidationHost requires Windows with the target InoProShop installation.')
    key = env.get('PlcMcp__ApiKey', '')
    if len(key.strip()) < 32: raise ValueError('Set PlcMcp__ApiKey to at least 32 random characters; it is never written to the bundle.')
    runtimes = subprocess.check_output([dotnet, '--list-runtimes'], text=True)
    for runtime in ('Microsoft.NETCore.App', 'Microsoft.AspNetCore.App'):
        if not re.search(r'^'+re.escape(runtime)+r' 8\.0\.', runtimes, re.MULTILINE): raise ValueError('Install the .NET 8 ASP.NET Core runtime for this machine.')
    data, workspace = Path(data).resolve(), Path(workspace).resolve()
    if root == data or root in data.parents or root == workspace or root in workspace.parents:
        raise ValueError('Keep runtime data and projects outside the immutable release bundle.')
    if mode == 'validation':
        for setting in ('PlcMcp__ValidationProfilePath', 'PlcMcp__Inovance__ExecutablePath'):
            value = Path(env.get(setting, ''))
            if not value.is_absolute() or not value.is_file(): raise ValueError('Set '+setting+' to an existing absolute file path.')
        profile = json.loads(Path(env['PlcMcp__ValidationProfilePath']).read_text(encoding='utf-8'))
        required = ('validationId', 'ideVersion', 'engineVersion', 'profile')
        if any(not profile.get(k) or 'REPLACE_' in profile[k] for k in required) or not profile.get('allowedOperations'):
            raise ValueError('Complete the validation identity, exact versions, Profile and operation allowlist.')
        if profile['profile'] != env.get('PlcMcp__Inovance__Profile'): raise ValueError('Executable Profile and validation Profile must agree.')
    acceptance_path = env.get('PlcMcp__Inovance__AcceptancePath')
    if mode == 'production' and acceptance_path:
        if sys.platform != 'win32': raise ValueError('Reviewed IDE execution requires Windows; omit AcceptancePath to use local tools.')
        for setting in ('PlcMcp__Inovance__ExecutablePath', 'PlcMcp__Inovance__EngineAssemblyPath', 'PlcMcp__Inovance__AcceptancePath'):
            path = Path(env.get(setting, ''))
            if not path.is_absolute() or not path.is_file(): raise ValueError('Set '+setting+' to an existing absolute file.')
    if mode != 'production': env.pop('PlcMcp__Inovance__AcceptancePath', None)
    env.update(PlcMcp__DataDirectory=str(data), PlcMcp__WorkspaceRoot=str(workspace),
               DOTNET_ENVIRONMENT='Development' if mode == 'fixture' else 'Production',
               DOTNET_HOST_PATH=str(Path(shutil.which(dotnet) or dotnet).resolve()))
    return manifest, [dotnet, str(root/manifest['entryPoint'])], env


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--bundle', required=True)
    parser.add_argument('--data-directory', required=True); parser.add_argument('--workspace-root', required=True)
    parser.add_argument('--dotnet', default='dotnet'); parser.add_argument('--check-only', action='store_true'); args = parser.parse_args()
    manifest, command, env = prepare(args.bundle, args.data_directory, args.workspace_root, args.dotnet)
    print(json.dumps(dict(mode=manifest['mode'], readiness='query_runtime_after_start', checked=True, foreground=not args.check_only)), flush=True)
    if args.check_only: return 0
    return subprocess.call(command, env=env, cwd=Path(args.bundle).resolve()/'bin')


if __name__ == '__main__': sys.exit(main())
