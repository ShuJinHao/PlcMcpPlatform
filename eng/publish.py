"""Create a framework-dependent .NET 8 bundle. Does not install or start a service."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import sys
from bundle import HOSTS, sha256, verify_bundle


def publish(mode, destination, dotnet='dotnet'):
    root = Path(__file__).resolve().parents[1]; output = Path(destination).resolve()
    if output.exists(): raise FileExistsError('Choose a new output directory; existing bundles are never overwritten.')
    project, entry = HOSTS[mode]
    subprocess.run([dotnet, 'restore', str(root/'PlcMcpPlatform.sln'), '--locked-mode'], cwd=root, check=True)
    output.mkdir(parents=True)
    subprocess.run([dotnet, 'publish', str(root/project), '-c', 'Release', '--no-restore', '-p:UseAppHost=false', '-o', str(output/'bin')], cwd=root, check=True)
    for directory in ('config', 'contracts', 'docs'):
        shutil.copytree(root/directory, output/directory, ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    (output/'eng').mkdir()
    helpers = ['bundle.py', 'start.py', 'doctor.py', 'transfer.py', 'mcp_client.py', 'run-regression.py', 'run-regressions.py', 'verify-opencode.py']
    if mode == 'fixture': helpers.append('init-fixture.py')
    for helper in helpers: shutil.copy2(root/'eng'/helper, output/'eng'/helper)
    for filename in ('README.md', 'LICENSE'):
        if (root/filename).is_file(): shutil.copy2(root/filename, output/filename)
    if mode == 'fixture':
        shutil.copytree(root/'tests/fixtures', output/'tests/fixtures')
    revision = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    dirty = bool(subprocess.check_output(['git', 'status', '--porcelain', '--untracked-files=normal'], cwd=root, text=True).strip())
    rows = [dict(path=p.relative_to(output).as_posix(), byteLength=p.stat().st_size, sha256=sha256(p))
            for p in sorted(output.rglob('*')) if p.is_file()]
    manifest = dict(schemaVersion=1, stage='P5', mode=mode, entryPoint='bin/'+entry, sourceCommit=revision,
                    sourceDirty=dirty, requiredRuntime='Microsoft.AspNetCore.App 8.0', engineeringReady=False,
                    evidenceKind='simulated' if mode == 'fixture' else 'unverified', files=rows)
    (output/'release.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2)+'\n', encoding='utf-8')
    verify_bundle(output)
    return dict(bundle=str(output), mode=mode, sourceCommit=revision, sourceDirty=dirty, files=len(rows), engineeringReady=False)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--mode', choices=HOSTS, default='production'); parser.add_argument('--output', required=True)
    parser.add_argument('--dotnet', default='dotnet'); args = parser.parse_args()
    print(json.dumps(publish(args.mode, args.output, args.dotnet))); return 0


if __name__ == '__main__': sys.exit(main())
