"""Check an explicitly installed OpenCode CLI against a running host without an LLM."""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from mcp_client import McpClient


def check(executable, url, key):
    # Validate loopback intent and authenticated HTTP before launching the client.
    client = McpClient(url, key); count = len(client.rpc('tools/list')['tools'])
    root = Path(__file__).resolve().parents[1]
    config = json.loads((root/'config/opencode.example.json').read_text(encoding='utf-8'))
    config['mcp']['plc']['url'] = url
    with tempfile.TemporaryDirectory(prefix='plcmcp-opencode-') as tmp:
        directory = Path(tmp)
        # Isolate configuration/data, and do not pass any model-provider credentials.
        env = {k:v for k,v in os.environ.items() if k.upper() in ('PATH','SYSTEMROOT','WINDIR','TEMP','TMP','LANG','LC_ALL','COMSPEC','PATHEXT')}
        env.update(XDG_CONFIG_HOME=str(directory/'config'), XDG_DATA_HOME=str(directory/'data'),
                   XDG_STATE_HOME=str(directory/'state'), XDG_CACHE_HOME=str(directory/'cache'),
                   OPENCODE_CONFIG_CONTENT=json.dumps(config), PlcMcp__ApiKey=key,
                   OPENCODE_DISABLE_AUTOUPDATE='true', OPENCODE_DISABLE_MODELS_FETCH='true', OPENCODE_DISABLE_LSP_DOWNLOAD='true',
                   OPENCODE_DISABLE_DEFAULT_PLUGINS='true', OPENCODE_DISABLE_CLAUDE_CODE='true', OPENCODE_PURE='true', NO_COLOR='1')
        def run(arguments):
            result = subprocess.run([executable, *arguments], cwd=directory, env=env, text=True, encoding='utf-8', errors='replace',
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, timeout=60)
            output = re.sub(r'\x1b\[[0-9;]*[A-Za-z]', '', result.stdout).replace(key, '[redacted]')
            if result.returncode: raise ValueError('OpenCode check failed: '+output[-4000:])
            return output.strip()
        version = run(['--version']); output = run(['mcp', 'list'])
        if not re.search(r'\bplc\s+connected\b', output): raise ValueError('OpenCode did not report a connected plc server: '+output[-4000:])
        return dict(openCodeVersion=version, connectionVerified=True, httpToolCount=count, llmGenerationVerified=False, engineeringReady=False)


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('--opencode', default='opencode'); parser.add_argument('--url', default='http://127.0.0.1:5088/mcp')
    args = parser.parse_args(); key = os.environ.get('PLCMCP_API_KEY') or os.environ.get('PlcMcp__ApiKey')
    if not key: parser.error('Set the MCP API key in the environment.')
    print(json.dumps(check(args.opencode, args.url, key))); return 0


if __name__ == '__main__': sys.exit(main())
