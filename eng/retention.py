"""Run the bundle's offline .NET maintenance entry point. No MCP or vendor process is started."""
import argparse
from pathlib import Path
import subprocess
from bundle import verify_bundle


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('action', choices=['preview', 'apply'])
    parser.add_argument('--bundle', required=True)
    parser.add_argument('--data-directory', required=True)
    parser.add_argument('--workspace-root', required=True)
    parser.add_argument('--policy')
    parser.add_argument('--report')
    parser.add_argument('--plan')
    parser.add_argument('--dotnet', default='dotnet')
    args = parser.parse_args()
    if args.action == 'preview' and (not args.report or args.plan) or args.action == 'apply' and (not args.plan or args.report or args.policy):
        parser.error('preview requires --report and optional --policy; apply requires only --plan.')
    root = Path(args.bundle).resolve(); manifest = verify_bundle(root)
    command = [args.dotnet, str(root/manifest['entryPoint']), '--maintenance', args.action,
               '--data-directory', str(Path(args.data_directory).resolve()), '--workspace-root', str(Path(args.workspace_root).resolve())]
    for key in ('policy', 'report', 'plan'):
        if getattr(args, key): command += ['--'+key, str(Path(getattr(args, key)).resolve())]
    return subprocess.call(command)


if __name__ == '__main__': raise SystemExit(main())
