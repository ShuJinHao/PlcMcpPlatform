"""Cross-platform repository checks. Real IDE/PLC and model-provider access are not required."""
import argparse
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--dotnet', default='dotnet'); args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    commands = [[args.dotnet, 'restore', 'PlcMcpPlatform.sln', '--locked-mode'],
                [args.dotnet, 'build', 'PlcMcpPlatform.sln', '-c', 'Release', '--no-restore'],
                [args.dotnet, 'test', 'PlcMcpPlatform.sln', '-c', 'Release', '--no-build', '--no-restore']]
    for check in ('mcp','execution','editing','configuration','exchange','delivery','incremental','members','graphs','publish'):
        commands.append([sys.executable, 'eng/verify-'+check+'.py', '--dotnet', args.dotnet])
    commands.append([sys.executable, '-m', 'unittest', 'discover', '-s', 'tests/script_engine', '-v'])
    for command in commands:
        print('Running: '+' '.join(command), flush=True)
        subprocess.run(command, cwd=root, check=True)
    print('Repository checks passed. Real IDE and LLM generation remain separate validation.'); return 0


if __name__ == '__main__': sys.exit(main())
